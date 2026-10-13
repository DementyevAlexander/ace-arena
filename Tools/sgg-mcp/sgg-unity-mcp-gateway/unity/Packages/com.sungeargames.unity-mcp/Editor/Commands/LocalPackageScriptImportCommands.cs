using System;
using System.IO;
using System.Security.Cryptography;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Paths;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.PackageManager;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class LocalPackageScriptImportCommands
    {
        const long MaxScriptBytes = 1024L * 1024L;
        const string SchemaJson = "{\"type\":\"object\",\"properties\":{\"package_script_path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":512,\"pattern\":\"^Packages/[^/]+/.+\\\\.cs$\"},\"dry_run\":{\"type\":\"boolean\",\"default\":true},\"precondition_sha256\":{\"type\":\"string\",\"pattern\":\"^[0-9a-f]{64}$\"}},\"required\":[\"package_script_path\"],\"additionalProperties\":false}";

        [McpCommand(
            "package.local_script.import",
            Title = "Import local package script",
            Description = "Import one exact bounded C# source file from a registered local or embedded package and request compilation convergence",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 30000,
            SchemaJson = SchemaJson,
            ResultShape = "mutation_result")]
        public static CommandExecutionResult Import(McpCommandContext context)
        {
            var requestedPath = JsonUtil.ExtractString(context.ArgsJson, "package_script_path");
            string packagePath;
            try
            {
                packagePath = PathGuard.RequireUnityAssetPath(requestedPath);
            }
            catch (Exception)
            {
                return CommandExecutionResult.ValidationError("invalid_field_value", "package_script_path must be one safe Unity-relative package path", "package_script_path", "path_not_allowed");
            }

            if (!TryPackageName(packagePath, out var packageName) || !packagePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                return CommandExecutionResult.ValidationError("invalid_field_value", "package_script_path must identify one C# file under Packages/<name>/", "package_script_path", "unsupported_package_script_path");
            }

            var package = FindRegisteredPackage(packageName);
            if (package == null)
            {
                return Refused(packagePath, "package_not_registered");
            }
            if (!string.Equals(package.assetPath == null ? string.Empty : package.assetPath.Replace('\\', '/'), "Packages/" + packageName, StringComparison.Ordinal))
            {
                return Refused(packagePath, "package_asset_root_mismatch");
            }

            var input = new LocalPackageCodeInventoryInput(package.source.ToString(), package.name, package.resolvedPath);
            if (!TryResolveTarget(input, packagePath, out var target, out var failure))
            {
                return Refused(packagePath, failure);
            }

            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            if (dryRun)
            {
                return CommandExecutionResult.Ok(ResultJson(packagePath, packageName, target, true, false, false, false));
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return CommandExecutionResult.Error("unity_busy", "Local package script import requires an idle Editor", "{\"reason\":\"editor_not_idle\",\"import_attempted\":false,\"script_compilation_requested\":false}");
            }

            var expectedSha256 = JsonUtil.ExtractString(context.ArgsJson, "precondition_sha256");
            if (string.IsNullOrEmpty(expectedSha256) || !string.Equals(expectedSha256, target.Sha256, StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("precondition_failed", "The exact local package script precondition is missing or changed", "{\"reason\":\"script_sha256_mismatch\",\"import_attempted\":false,\"script_compilation_requested\":false,\"current_sha256\":" + JsonUtil.String(target.Sha256) + "}");
            }
            if (!TryResolveTarget(input, packagePath, out var revalidated, out failure) || !string.Equals(revalidated.Sha256, target.Sha256, StringComparison.Ordinal))
            {
                return Refused(packagePath, string.IsNullOrEmpty(failure) ? "script_changed_during_revalidation" : failure);
            }

            AssetDatabase.ImportAsset(packagePath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var compilationAlreadyActive = EditorApplication.isCompiling;
            if (!compilationAlreadyActive) CompilationPipeline.RequestScriptCompilation();
            UnityBridgeRuntime.MarkPackageRefreshObserved("package.local_script.import");

            var compilerIncluded = CompilerIncludes(input, packagePath);
            return CommandExecutionResult.Ok(ResultJson(packagePath, packageName, revalidated, false, true, !compilationAlreadyActive, compilerIncluded));
        }

        internal static bool TryResolveTargetForTests(LocalPackageCodeInventoryInput package, string packagePath, out string sha256, out string failure)
        {
            var success = TryResolveTarget(package, packagePath, out var target, out failure);
            sha256 = success ? target.Sha256 : string.Empty;
            return success;
        }

        static UnityEditor.PackageManager.PackageInfo FindRegisteredPackage(string packageName)
        {
            var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
            for (var index = 0; index < packages.Length; index++)
            {
                if (string.Equals(packages[index].name, packageName, StringComparison.Ordinal)) return packages[index];
            }
            return null;
        }

        static bool TryResolveTarget(LocalPackageCodeInventoryInput package, string packagePath, out LocalPackageScriptImportTarget target, out string failure)
        {
            target = null;
            failure = string.Empty;
            if (package == null || !(string.Equals(package.Source, PackageSource.Local.ToString(), StringComparison.Ordinal) || string.Equals(package.Source, PackageSource.Embedded.ToString(), StringComparison.Ordinal)))
            {
                failure = "unsupported_package_source";
                return false;
            }
            if (!TryPackageName(packagePath, out var packageName) || !string.Equals(packageName, package.Name, StringComparison.Ordinal) || !packagePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                failure = "package_identity_mismatch";
                return false;
            }

            try
            {
                if (string.IsNullOrEmpty(package.ResolvedPath) || package.ResolvedPath.StartsWith("\\\\", StringComparison.Ordinal) || package.ResolvedPath.StartsWith("//", StringComparison.Ordinal))
                {
                    failure = "resolved_path_unavailable";
                    return false;
                }
                var root = Trim(Path.GetFullPath(package.ResolvedPath));
                var relative = packagePath.Substring(("Packages/" + packageName + "/").Length).Replace('/', Path.DirectorySeparatorChar);
                var fullPath = Path.GetFullPath(Path.Combine(root, relative));
                if (!IsSameOrChild(fullPath, root) || IsNetworkDrive(root) || HasReparsePoint(root, fullPath) || !File.Exists(fullPath))
                {
                    failure = "path_security_rejected";
                    return false;
                }
                var info = new FileInfo(fullPath);
                if (info.Length < 0 || info.Length > MaxScriptBytes)
                {
                    failure = "script_size_limit_exceeded";
                    return false;
                }
                target = new LocalPackageScriptImportTarget(info.Length, Sha256(fullPath));
                return true;
            }
            catch
            {
                failure = "path_security_rejected";
                return false;
            }
        }

        static bool CompilerIncludes(LocalPackageCodeInventoryInput package, string packagePath)
        {
            return LocalPackageCodeInventoryInspector.TryCaptureCompilationSourcePaths(new[] { package }, out var paths, out _)
                && Array.IndexOf(paths, packagePath) >= 0;
        }

        static CommandExecutionResult Refused(string packagePath, string reason)
        {
            return CommandExecutionResult.Error("package_script_import_refused", "Local package script import validation failed", "{\"reason\":" + JsonUtil.String(reason) + ",\"package_script_path\":" + JsonUtil.String(packagePath) + ",\"import_attempted\":false,\"script_compilation_requested\":false}");
        }

        static string ResultJson(string packagePath, string packageName, LocalPackageScriptImportTarget target, bool dryRun, bool importAttempted, bool compilationRequested, bool compilerIncluded)
        {
            return "{\"dry_run\":" + JsonUtil.Bool(dryRun)
                + ",\"package_script_path\":" + JsonUtil.String(packagePath)
                + ",\"package_name\":" + JsonUtil.String(packageName)
                + ",\"package_source_scope\":\"local_or_embedded\""
                + ",\"size_bytes\":" + target.Size
                + ",\"precondition_sha256\":" + JsonUtil.String(target.Sha256)
                + ",\"import_attempted\":" + JsonUtil.Bool(importAttempted)
                + ",\"script_compilation_requested\":" + JsonUtil.Bool(compilationRequested)
                + ",\"compiler_inclusion_observed\":" + JsonUtil.Bool(compilerIncluded)
                + ",\"converged\":" + JsonUtil.Bool(compilerIncluded)
                + ",\"manifest_mutated\":false,\"packages_lock_mutated\":false,\"implicit_retry\":false"
                + ",\"next_valid_actions\":[\"compile.wait_ready\",\"compile.status\"]}";
        }

        static bool TryPackageName(string packagePath, out string packageName)
        {
            packageName = string.Empty;
            if (string.IsNullOrEmpty(packagePath) || !packagePath.StartsWith("Packages/", StringComparison.Ordinal)) return false;
            var separator = packagePath.IndexOf('/', "Packages/".Length);
            if (separator <= "Packages/".Length) return false;
            packageName = packagePath.Substring("Packages/".Length, separator - "Packages/".Length);
            return packageName.Length <= 128;
        }

        static bool HasReparsePoint(string root, string fullPath)
        {
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0) return true;
            var directory = new DirectoryInfo(Path.GetDirectoryName(fullPath));
            while (directory != null && IsSameOrChild(directory.FullName, root))
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return true;
                if (string.Equals(Trim(directory.FullName), root, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
                directory = directory.Parent;
            }
            return false;
        }

        static bool IsNetworkDrive(string fullPath)
        {
            var root = Path.GetPathRoot(fullPath);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }

        static bool IsSameOrChild(string fullPath, string root)
        {
            var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var candidate = Trim(Path.GetFullPath(fullPath));
            var canonicalRoot = Trim(Path.GetFullPath(root));
            return string.Equals(candidate, canonicalRoot, comparison) || candidate.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, comparison);
        }

        static string Trim(string fullPath)
        {
            var root = Path.GetPathRoot(fullPath);
            return !string.IsNullOrEmpty(root) && string.Equals(root, fullPath, StringComparison.OrdinalIgnoreCase)
                ? root
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        static string Sha256(string fullPath)
        {
            using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(stream);
                var chars = new char[hash.Length * 2];
                const string alphabet = "0123456789abcdef";
                for (var index = 0; index < hash.Length; index++)
                {
                    chars[index * 2] = alphabet[hash[index] >> 4];
                    chars[index * 2 + 1] = alphabet[hash[index] & 15];
                }
                return new string(chars);
            }
        }

        sealed class LocalPackageScriptImportTarget
        {
            public LocalPackageScriptImportTarget(long size, string sha256)
            {
                Size = size;
                Sha256 = sha256;
            }

            public long Size { get; private set; }
            public string Sha256 { get; private set; }
        }
    }
}
