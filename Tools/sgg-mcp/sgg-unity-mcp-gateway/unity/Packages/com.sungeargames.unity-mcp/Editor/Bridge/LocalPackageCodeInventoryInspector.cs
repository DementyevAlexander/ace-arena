using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.PackageManager;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace SGG.UnityMCP.Editor.Bridge
{
    internal static class LocalPackageCodeInventoryInspector
    {
        const int MaxLocalPackages = 64;
        const int MaxDirectories = 4096;
        const int MaxSourceFiles = 16384;
        const int MaxAssetDatabasePaths = 500000;
        const long MaxScanMilliseconds = 30000;
        const int MaxPackageNameChars = 128;
        const int MaxMismatchDetails = 16;
        const int MaxDiagnosticPathChars = 512;
        const int MaxMetaFileBytes = 64 * 1024;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
        static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        static extern SafeFileHandle OpenMetadataHandle(string path, uint access, uint share, IntPtr securityAttributes, uint disposition, uint flags, IntPtr template);

        [StructLayout(LayoutKind.Sequential)]
        struct FileAttributeTagInfo
        {
            public uint FileAttributes;
            public uint ReparseTag;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out FileAttributeTagInfo info, uint size);

        static readonly HashSet<string> CodeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".asmdef",
            ".asmref",
            ".cs",
            ".dll",
            ".rsp",
        };
        static readonly byte[] LineSeparator = { (byte)'\n' };

        public static LocalPackageCodeInventorySnapshot InspectRegisteredPackages(PackageInfo[] packages)
        {
            var budget = Stopwatch.StartNew();
            if (!TryBuildInputs(packages, budget, MaxScanMilliseconds, out var inputs, out var inputFailure))
            {
                return LocalPackageCodeInventorySnapshot.Unavailable(inputFailure);
            }

            string[] assetPaths;
            try
            {
                assetPaths = AssetDatabase.GetAllAssetPaths();
            }
            catch
            {
                return LocalPackageCodeInventorySnapshot.Unavailable("read_exception");
            }
            if (BudgetExceeded(budget, MaxScanMilliseconds))
            {
                return LocalPackageCodeInventorySnapshot.Unavailable("budget_exhausted");
            }

            if (!TryCaptureCompilationSourcePaths(inputs, assetPaths, out var compilationSourcePaths, out var expectedCompilationSourcePaths, out var compilationFailure))
            {
                return LocalPackageCodeInventorySnapshot.Unavailable(compilationFailure);
            }

            return InspectInputs(inputs, assetPaths, compilationSourcePaths, expectedCompilationSourcePaths, budget, MaxScanMilliseconds, MaxSourceFiles);
        }

        internal static bool TryCaptureRegisteredPackages(PackageInfo[] packages, out LocalPackageCodeInventoryInput[] inputs, out string failure)
        {
            return TryBuildInputs(packages, Stopwatch.StartNew(), MaxScanMilliseconds, out inputs, out failure);
        }

        internal static LocalPackageCodeInventorySnapshot InspectCapturedInputs(LocalPackageCodeInventoryInput[] packages, string[] assetPaths, string[] compilationSourcePaths, string[] expectedCompilationSourcePaths)
        {
            return InspectInputs(packages, assetPaths, compilationSourcePaths, expectedCompilationSourcePaths, Stopwatch.StartNew(), MaxScanMilliseconds, MaxSourceFiles);
        }

        internal static LocalPackageCodeInventorySnapshot InspectForTests(LocalPackageCodeInventoryInput[] packages, string[] assetPaths, long maxScanMilliseconds = MaxScanMilliseconds, int maxSourceFiles = MaxSourceFiles, string[] compilationSourcePaths = null, string[] expectedCompilationSourcePaths = null)
        {
            return InspectInputs(
                packages,
                assetPaths,
                compilationSourcePaths ?? CompilationSourcePathsFromAssetPaths(assetPaths),
                expectedCompilationSourcePaths ?? CompilationSourcePathsFromAssetPaths(assetPaths),
                Stopwatch.StartNew(),
                maxScanMilliseconds,
                maxSourceFiles);
        }

        internal static bool TryCaptureCompilationSourcePaths(LocalPackageCodeInventoryInput[] packages, out string[] result, out string failure)
        {
            return TryCaptureCompilationSourcePaths(packages, null, out result, out _, out failure);
        }

        internal static bool TryCaptureCompilationSourcePaths(LocalPackageCodeInventoryInput[] packages, string[] assetPaths, out string[] result, out string[] expectedResult, out string failure)
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            var expectedPaths = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var assemblies = CompilationPipeline.GetAssemblies();
                var activeAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var assemblyIndex = 0; assemblyIndex < assemblies.Length; assemblyIndex++)
                {
                    activeAssemblyNames.Add(NormalizeAssemblyName(assemblies[assemblyIndex].name));
                    var sources = assemblies[assemblyIndex].sourceFiles;
                    for (var sourceIndex = 0; sourceIndex < sources.Length; sourceIndex++)
                    {
                        if (!TryNormalizeCompilationSourcePath(sources[sourceIndex], packages, out var assetPath)) continue;
                        if (!paths.Contains(assetPath) && paths.Count >= MaxSourceFiles)
                        {
                            result = new string[0];
                            expectedResult = new string[0];
                            failure = "compiler_source_limit_exceeded";
                            return false;
                        }
                        paths.Add(assetPath);
                    }
                }

                for (var assetIndex = 0; assetPaths != null && assetIndex < assetPaths.Length; assetIndex++)
                {
                    var assetPath = NormalizeAssetPath(assetPaths[assetIndex]);
                    if (!TryLocalPackageName(assetPath, out var packageName)
                        || !ContainsPackage(packages, packageName)
                        || !string.Equals(Path.GetExtension(assetPath), ".cs", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var assemblyName = NormalizeAssemblyName(CompilationPipeline.GetAssemblyNameFromScriptPath(assetPath));
                    if (!string.IsNullOrEmpty(assemblyName) && activeAssemblyNames.Contains(assemblyName)) expectedPaths.Add(assetPath);
                }
            }
            catch
            {
                result = new string[0];
                expectedResult = new string[0];
                failure = "compiler_source_read_exception";
                return false;
            }

            result = new List<string>(paths).ToArray();
            expectedResult = new List<string>(expectedPaths).ToArray();
            failure = string.Empty;
            return true;
        }

        static bool TryBuildInputs(PackageInfo[] packages, Stopwatch budget, long maxScanMilliseconds, out LocalPackageCodeInventoryInput[] result, out string failure)
        {
            var inputs = new List<LocalPackageCodeInventoryInput>();
            var count = packages == null ? 0 : packages.Length;
            for (var index = 0; index < count; index++)
            {
                if (BudgetExceeded(budget, maxScanMilliseconds))
                {
                    result = new LocalPackageCodeInventoryInput[0];
                    failure = "budget_exhausted";
                    return false;
                }

                var package = packages[index];
                var source = package == null ? string.Empty : package.source.ToString();
                if (package == null || !IsMutableLocalSource(source))
                {
                    continue;
                }
                if (inputs.Count >= MaxLocalPackages)
                {
                    result = new LocalPackageCodeInventoryInput[0];
                    failure = "package_limit_exceeded";
                    return false;
                }

                inputs.Add(new LocalPackageCodeInventoryInput(source, package.name, package.resolvedPath));
            }
            result = inputs.ToArray();
            failure = string.Empty;
            return true;
        }

        static LocalPackageCodeInventorySnapshot InspectInputs(LocalPackageCodeInventoryInput[] packages, string[] assetPaths, string[] compilationSourcePaths, string[] expectedCompilationSourcePaths, Stopwatch budget, long maxScanMilliseconds, int maxSourceFiles)
        {
            if (packages == null || assetPaths == null || compilationSourcePaths == null || expectedCompilationSourcePaths == null)
            {
                return LocalPackageCodeInventorySnapshot.Unavailable("input_unavailable");
            }
            if (packages.Length > MaxLocalPackages)
            {
                return LocalPackageCodeInventorySnapshot.Unavailable("package_limit_exceeded", packages.Length);
            }
            if (assetPaths.Length > MaxAssetDatabasePaths)
            {
                return LocalPackageCodeInventorySnapshot.Unavailable("asset_path_limit_exceeded", packages.Length);
            }
            if (compilationSourcePaths.Length > MaxSourceFiles)
            {
                return LocalPackageCodeInventorySnapshot.Unavailable("compiler_source_limit_exceeded", packages.Length);
            }
            if (expectedCompilationSourcePaths.Length > MaxSourceFiles)
            {
                return LocalPackageCodeInventorySnapshot.Unavailable("compiler_source_limit_exceeded", packages.Length);
            }
            if (BudgetExceeded(budget, maxScanMilliseconds))
            {
                return LocalPackageCodeInventorySnapshot.Unavailable("budget_exhausted");
            }

            var effectiveMaxSourceFiles = Math.Min(MaxSourceFiles, Math.Max(1, maxSourceFiles));
            var sourcePaths = new HashSet<string>(StringComparer.Ordinal);
            var sourceState = new List<string>();
            var sourceFileLocations = new Dictionary<string, string>(StringComparer.Ordinal);
            var sourceMetadata = new Dictionary<string, LocalPackageSourceMetadataEvidence>(StringComparer.Ordinal);
            var localPackageNames = new HashSet<string>(StringComparer.Ordinal);
            var directoryCount = 0;
            for (var index = 0; index < packages.Length; index++)
            {
                var package = packages[index];
                if (package == null || !IsMutableLocalSource(package.Source))
                {
                    continue;
                }
                if (!IsSafePackageName(package.Name) || !localPackageNames.Add(package.Name))
                {
                    return LocalPackageCodeInventorySnapshot.Unavailable("package_identity_invalid", localPackageNames.Count);
                }
                if (!TryScanPackage(package, sourcePaths, sourceState, sourceFileLocations, budget, maxScanMilliseconds, effectiveMaxSourceFiles, ref directoryCount, out var failure))
                {
                    return LocalPackageCodeInventorySnapshot.Unavailable(failure, localPackageNames.Count, sourcePaths.Count);
                }
            }

            var importedPaths = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < assetPaths.Length; index++)
            {
                if (BudgetExceeded(budget, maxScanMilliseconds))
                {
                    return LocalPackageCodeInventorySnapshot.Unavailable("budget_exhausted", localPackageNames.Count, sourcePaths.Count, importedPaths.Count);
                }

                var assetPath = NormalizeAssetPath(assetPaths[index]);
                if (!TryLocalPackageName(assetPath, out var packageName)
                    || !localPackageNames.Contains(packageName)
                    || !ShouldTrack(assetPath))
                {
                    continue;
                }
                if (!IsSafePackageAssetPath(assetPaths[index], assetPath)) return LocalPackageCodeInventorySnapshot.Unavailable("path_security_rejected", localPackageNames.Count, sourcePaths.Count, importedPaths.Count);
                if (!importedPaths.Contains(assetPath) && importedPaths.Count >= effectiveMaxSourceFiles)
                {
                    return LocalPackageCodeInventorySnapshot.Unavailable("file_limit_exceeded", localPackageNames.Count, sourcePaths.Count, importedPaths.Count);
                }
                importedPaths.Add(assetPath);
            }

            var compilerPaths = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < compilationSourcePaths.Length; index++)
            {
                var compilerPath = NormalizeAssetPath(compilationSourcePaths[index]);
                if (!TryLocalPackageName(compilerPath, out var packageName)
                    || !localPackageNames.Contains(packageName)
                    || !string.Equals(Path.GetExtension(compilerPath), ".cs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!IsSafePackageAssetPath(compilationSourcePaths[index], compilerPath)) return LocalPackageCodeInventorySnapshot.Unavailable("path_security_rejected", localPackageNames.Count, sourcePaths.Count, importedPaths.Count);
                compilerPaths.Add(compilerPath);
            }

            var expectedCompilerPaths = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < expectedCompilationSourcePaths.Length; index++)
            {
                var expectedPath = NormalizeAssetPath(expectedCompilationSourcePaths[index]);
                if (!TryLocalPackageName(expectedPath, out var packageName)
                    || !localPackageNames.Contains(packageName)
                    || !string.Equals(Path.GetExtension(expectedPath), ".cs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!IsSafePackageAssetPath(expectedCompilationSourcePaths[index], expectedPath)) return LocalPackageCodeInventorySnapshot.Unavailable("path_security_rejected", localPackageNames.Count, sourcePaths.Count, importedPaths.Count);
                expectedCompilerPaths.Add(expectedPath);
            }

            var sortedSourcePaths = new List<string>(sourcePaths);
            var sortedImportedPaths = new List<string>(importedPaths);
            var sortedCompilerPaths = new List<string>(compilerPaths);
            var sortedExpectedCompilerPaths = new List<string>(expectedCompilerPaths);
            sortedSourcePaths.Sort(StringComparer.Ordinal);
            if (BudgetExceeded(budget, maxScanMilliseconds))
            {
                return LocalPackageCodeInventorySnapshot.Unavailable("budget_exhausted", localPackageNames.Count, sourcePaths.Count, importedPaths.Count);
            }
            sortedImportedPaths.Sort(StringComparer.Ordinal);
            if (BudgetExceeded(budget, maxScanMilliseconds))
            {
                return LocalPackageCodeInventorySnapshot.Unavailable("budget_exhausted", localPackageNames.Count, sourcePaths.Count, importedPaths.Count);
            }
            sortedCompilerPaths.Sort(StringComparer.Ordinal);
            if (BudgetExceeded(budget, maxScanMilliseconds))
            {
                return LocalPackageCodeInventorySnapshot.Unavailable("budget_exhausted", localPackageNames.Count, sourcePaths.Count, importedPaths.Count);
            }
            sortedExpectedCompilerPaths.Sort(StringComparer.Ordinal);
            if (BudgetExceeded(budget, maxScanMilliseconds))
            {
                return LocalPackageCodeInventorySnapshot.Unavailable("budget_exhausted", localPackageNames.Count, sourcePaths.Count, importedPaths.Count);
            }
            sourceState.Sort(StringComparer.Ordinal);
            if (BudgetExceeded(budget, maxScanMilliseconds))
            {
                return LocalPackageCodeInventorySnapshot.Unavailable("budget_exhausted", localPackageNames.Count, sourcePaths.Count, importedPaths.Count);
            }
            if (!TrySha256Lines(sortedSourcePaths, budget, maxScanMilliseconds, out var sourceFingerprint)
                || !TrySha256Lines(sortedImportedPaths, budget, maxScanMilliseconds, out var assetDatabaseFingerprint)
                || !TrySha256Lines(sortedCompilerPaths, budget, maxScanMilliseconds, out var compilerSourceFingerprint)
                || !TrySha256Lines(sourceState, budget, maxScanMilliseconds, out var sourceStateFingerprint))
            {
                return LocalPackageCodeInventorySnapshot.Unavailable("budget_exhausted", localPackageNames.Count, sourcePaths.Count, importedPaths.Count);
            }
            var missingAssetPaths = Difference(sortedSourcePaths, importedPaths, null);
            var unexpectedAssetPaths = Difference(sortedImportedPaths, sourcePaths, null);
            var missingCompilerPaths = Difference(sortedExpectedCompilerPaths, compilerPaths, sourcePaths);
            var unexpectedCompilerPaths = Difference(sortedCompilerPaths, sourcePaths, null);
            var metadataProbes = 0;
            for (var index = 0; index < missingAssetPaths.Count && metadataProbes < MaxMismatchDetails; index++)
            {
                if (BudgetExceeded(budget, maxScanMilliseconds)) return LocalPackageCodeInventorySnapshot.Unavailable("budget_exhausted", localPackageNames.Count, sourcePaths.Count, importedPaths.Count);
                var assetPath = missingAssetPaths[index];
                if (assetPath.Length > MaxDiagnosticPathChars || !sourceFileLocations.TryGetValue(assetPath, out var sourceFile)) continue;
                metadataProbes++;
                if (!TryInspectSourceMetadata(sourceFile, out var metadataEvidence, budget, maxScanMilliseconds, out var metadataFailure))
                {
                    return LocalPackageCodeInventorySnapshot.Unavailable(metadataFailure, localPackageNames.Count, sourcePaths.Count, importedPaths.Count);
                }
                if (metadataEvidence != null) sourceMetadata[assetPath] = metadataEvidence;
            }
            var mismatchDetails = new List<LocalPackageInventoryMismatchDetail>();
            AppendMismatchDetails(mismatchDetails, "missing_asset", missingAssetPaths, sourceMetadata);
            AppendMismatchDetails(mismatchDetails, "unexpected_asset", unexpectedAssetPaths, null);
            AppendMismatchDetails(mismatchDetails, "missing_compiler_source", missingCompilerPaths, null);
            AppendMismatchDetails(mismatchDetails, "unexpected_compiler_source", unexpectedCompilerPaths, null);
            var mismatchDetailTotalCount = missingAssetPaths.Count + unexpectedAssetPaths.Count + missingCompilerPaths.Count + unexpectedCompilerPaths.Count;
            var mismatchPackage = FirstMismatchPackage(missingAssetPaths, unexpectedAssetPaths, missingCompilerPaths, unexpectedCompilerPaths);
            if (BudgetExceeded(budget, maxScanMilliseconds)) return LocalPackageCodeInventorySnapshot.Unavailable("budget_exhausted", localPackageNames.Count, sourcePaths.Count, importedPaths.Count);
            return LocalPackageCodeInventorySnapshot.Available(
                localPackageNames.Count,
                sourcePaths.Count,
                importedPaths.Count,
                missingAssetPaths.Count,
                unexpectedAssetPaths.Count,
                mismatchPackage,
                sourceFingerprint,
                assetDatabaseFingerprint,
                sourceStateFingerprint,
                compilerPaths.Count,
                missingCompilerPaths.Count,
                unexpectedCompilerPaths.Count,
                compilerSourceFingerprint,
                mismatchDetails.ToArray(),
                mismatchDetailTotalCount);
        }

        static List<string> Difference(List<string> candidates, HashSet<string> excluded, HashSet<string> required)
        {
            var result = new List<string>();
            for (var index = 0; index < candidates.Count; index++)
            {
                var path = candidates[index];
                if ((required == null || required.Contains(path)) && !excluded.Contains(path)) result.Add(path);
            }
            return result;
        }

        static void AppendMismatchDetails(List<LocalPackageInventoryMismatchDetail> details, string kind, List<string> paths, Dictionary<string, LocalPackageSourceMetadataEvidence> metadata)
        {
            for (var index = 0; index < paths.Count && details.Count < MaxMismatchDetails; index++)
            {
                var path = paths[index];
                if (string.IsNullOrEmpty(path) || path.Length > MaxDiagnosticPathChars || !IsSafePackageAssetPath(path, path)) continue;
                LocalPackageSourceMetadataEvidence metadataEvidence = null;
                if (metadata != null) metadata.TryGetValue(path, out metadataEvidence);
                details.Add(new LocalPackageInventoryMismatchDetail(kind, path, metadataEvidence == null ? string.Empty : metadataEvidence.Reason, metadataEvidence == null ? 0 : metadataEvidence.ObservedGuidLength));
            }
        }

        static string FirstMismatchPackage(params List<string>[] pathGroups)
        {
            for (var groupIndex = 0; groupIndex < pathGroups.Length; groupIndex++)
            {
                var paths = pathGroups[groupIndex];
                if (paths != null && paths.Count > 0 && TryLocalPackageName(paths[0], out var packageName)) return packageName;
            }
            return string.Empty;
        }

        static string[] CompilationSourcePathsFromAssetPaths(string[] assetPaths)
        {
            if (assetPaths == null) return null;
            var paths = new List<string>();
            for (var index = 0; index < assetPaths.Length; index++)
            {
                if (string.Equals(Path.GetExtension(assetPaths[index]), ".cs", StringComparison.OrdinalIgnoreCase)) paths.Add(assetPaths[index]);
            }
            return paths.ToArray();
        }

        static string NormalizeAssemblyName(string assemblyName)
        {
            var name = Path.GetFileName(assemblyName ?? string.Empty);
            return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
        }

        static bool TryNormalizeCompilationSourcePath(string sourcePath, LocalPackageCodeInventoryInput[] packages, out string assetPath)
        {
            assetPath = string.Empty;
            var normalized = NormalizeAssetPath(sourcePath);
            if (normalized.StartsWith("Packages/", StringComparison.Ordinal))
            {
                if (TryLocalPackageName(normalized, out var packageName) && ContainsPackage(packages, packageName)) assetPath = normalized;
                return !string.IsNullOrEmpty(assetPath);
            }
            if (!Path.IsPathRooted(sourcePath)) return false;

            for (var index = 0; packages != null && index < packages.Length; index++)
            {
                var package = packages[index];
                if (package == null || !IsMutableLocalSource(package.Source) || string.IsNullOrEmpty(package.ResolvedPath)) continue;
                try
                {
                    var fullSource = Path.GetFullPath(sourcePath);
                    var root = TrimTrailingSeparators(Path.GetFullPath(package.ResolvedPath));
                    if (!IsSameOrChildPath(fullSource, root)) continue;
                    var relative = fullSource.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                    assetPath = "Packages/" + package.Name + "/" + relative;
                    return true;
                }
                catch
                {
                    return false;
                }
            }
            return false;
        }

        static bool ContainsPackage(LocalPackageCodeInventoryInput[] packages, string packageName)
        {
            for (var index = 0; packages != null && index < packages.Length; index++)
            {
                var package = packages[index];
                if (package != null && IsMutableLocalSource(package.Source) && string.Equals(package.Name, packageName, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        static bool TryScanPackage(LocalPackageCodeInventoryInput package, HashSet<string> sourcePaths, List<string> sourceState, Dictionary<string, string> sourceFileLocations, Stopwatch budget, long maxScanMilliseconds, int maxSourceFiles, ref int directoryCount, out string failure)
        {
            failure = string.Empty;
            if (string.IsNullOrEmpty(package.ResolvedPath)
                || package.ResolvedPath.StartsWith("\\\\", StringComparison.Ordinal)
                || package.ResolvedPath.StartsWith("//", StringComparison.Ordinal))
            {
                failure = "resolved_path_unavailable";
                return false;
            }

            string root;
            try
            {
                root = TrimTrailingSeparators(Path.GetFullPath(package.ResolvedPath));
                if (!Directory.Exists(root) || IsNetworkDrive(root) || HasReparsePointInPath(root))
                {
                    failure = "path_security_rejected";
                    return false;
                }
            }
            catch
            {
                failure = "path_security_rejected";
                return false;
            }

            var pending = new Stack<string>();
            pending.Push(root);
            try
            {
                while (pending.Count > 0)
                {
                    if (BudgetExceeded(budget, maxScanMilliseconds))
                    {
                        failure = "budget_exhausted";
                        return false;
                    }
                    if (++directoryCount > MaxDirectories)
                    {
                        failure = "directory_limit_exceeded";
                        return false;
                    }

                    var directory = Path.GetFullPath(pending.Pop());
                    if (!IsSameOrChildPath(directory, root) || (new DirectoryInfo(directory).Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        failure = "path_security_rejected";
                        return false;
                    }

                    foreach (var childPath in Directory.EnumerateDirectories(directory))
                    {
                        if (BudgetExceeded(budget, maxScanMilliseconds))
                        {
                            failure = "budget_exhausted";
                            return false;
                        }
                        if (directoryCount + pending.Count >= MaxDirectories)
                        {
                            failure = "directory_limit_exceeded";
                            return false;
                        }

                        var child = Path.GetFullPath(childPath);
                        if (IsUnityIgnoredPackageDirectory(child))
                        {
                            continue;
                        }
                        if (!IsSameOrChildPath(child, root) || (new DirectoryInfo(child).Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            failure = "path_security_rejected";
                            return false;
                        }
                        pending.Push(child);
                    }

                    foreach (var filePath in Directory.EnumerateFiles(directory))
                    {
                        if (BudgetExceeded(budget, maxScanMilliseconds))
                        {
                            failure = "budget_exhausted";
                            return false;
                        }

                        var file = Path.GetFullPath(filePath);
                        if (!ShouldTrack(file))
                        {
                            continue;
                        }
                        if (BudgetExceeded(budget, maxScanMilliseconds))
                        {
                            failure = "budget_exhausted";
                            return false;
                        }
                        if (!IsSameOrChildPath(file, root) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                        {
                            failure = "path_security_rejected";
                            return false;
                        }
                        var relative = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                        var assetPath = "Packages/" + package.Name + "/" + relative;
                        if (sourcePaths.Contains(assetPath))
                        {
                            failure = "duplicate_asset_path";
                            return false;
                        }
                        if (sourcePaths.Count >= maxSourceFiles)
                        {
                            failure = "file_limit_exceeded";
                            return false;
                        }
                        sourcePaths.Add(assetPath);
                        sourceFileLocations.Add(assetPath, file);
                        var info = new FileInfo(file);
                        sourceState.Add(assetPath + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks);
                        if (BudgetExceeded(budget, maxScanMilliseconds))
                        {
                            failure = "budget_exhausted";
                            return false;
                        }
                    }
                }
            }
            catch
            {
                failure = "read_exception";
                return false;
            }
            return true;
        }

        static bool TryInspectSourceMetadata(string sourceFile, out LocalPackageSourceMetadataEvidence evidence, Stopwatch budget, long maxScanMilliseconds, out string failure)
        {
            evidence = null;
            failure = string.Empty;
            var metaPath = sourceFile + ".meta";
            try
            {
                if (BudgetExceeded(budget, maxScanMilliseconds)) { failure = "budget_exhausted"; return false; }
                var fullMetaPath = Path.GetFullPath(metaPath);
                if (!string.Equals(fullMetaPath, metaPath, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    || HasReparsePointInPath(Path.GetDirectoryName(fullMetaPath)))
                {
                    failure = "path_security_rejected";
                    return false;
                }
                if (Path.DirectorySeparatorChar != '\\')
                {
                    evidence = new LocalPackageSourceMetadataEvidence("meta_inspection_unavailable", 0);
                    return true;
                }
                using (var handle = OpenMetadataHandle(fullMetaPath, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
                {
                    if (handle == null || handle.IsInvalid)
                    {
                        if (Marshal.GetLastWin32Error() == 2) { evidence = new LocalPackageSourceMetadataEvidence("meta_missing", 0); return true; }
                        failure = "read_exception";
                        return false;
                    }
                    if (!GetFileInformationByHandleEx(handle, 9, out var fileInfo, 8)
                        || (fileInfo.FileAttributes & 0x400) != 0)
                    {
                        failure = "path_security_rejected";
                        return false;
                    }
                    var finalPath = new StringBuilder(4096);
                    var finalLength = GetFinalPathNameByHandle(handle, finalPath, (uint)finalPath.Capacity, 0);
                    if (finalLength == 0 || finalLength >= finalPath.Capacity || !string.Equals(
                        finalPath.ToString(), "\\\\?\\" + fullMetaPath,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        failure = "path_security_rejected";
                        return false;
                    }
                    using (var stream = new FileStream(handle, FileAccess.Read))
                    {
                        if (stream.Length > MaxMetaFileBytes)
                        {
                            evidence = new LocalPackageSourceMetadataEvidence("meta_size_limit_exceeded", 0);
                            return true;
                        }
                        var bytes = new byte[MaxMetaFileBytes + 1];
                        var length = 0;
                        while (length < bytes.Length)
                        {
                            if (BudgetExceeded(budget, maxScanMilliseconds)) { failure = "budget_exhausted"; return false; }
                            var count = stream.Read(bytes, length, bytes.Length - length);
                            if (count == 0) break;
                            length += count;
                        }
                        if (length > MaxMetaFileBytes || stream.Length > MaxMetaFileBytes)
                        {
                            evidence = new LocalPackageSourceMetadataEvidence("meta_size_limit_exceeded", 0);
                            return true;
                        }
                        var text = Encoding.UTF8.GetString(bytes, 0, length);
                        string guid = null;
                        var guidCount = 0;
                        using (var reader = new StringReader(text))
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                if (BudgetExceeded(budget, maxScanMilliseconds)) { failure = "budget_exhausted"; return false; }
                                if (!line.StartsWith("guid:", StringComparison.Ordinal)) continue;
                                guidCount++;
                                if (guidCount == 1) guid = line.Substring("guid:".Length).Trim();
                            }
                        }
                        var observedLength = guid == null ? 0 : guid.Length;
                        var reason = guidCount == 0 || string.IsNullOrEmpty(guid) ? "guid_missing"
                            : guidCount > 1 ? "guid_ambiguous"
                            : guid.Length != 32 ? "guid_invalid_length"
                            : !IsHex(guid) ? "guid_invalid_format"
                            : string.Empty;
                        if (!string.IsNullOrEmpty(reason)) evidence = new LocalPackageSourceMetadataEvidence(reason, observedLength);
                        return true;
                    }
                }
            }
            catch (FileNotFoundException)
            {
                evidence = new LocalPackageSourceMetadataEvidence("meta_missing", 0);
                return true;
            }
            catch
            {
                failure = "read_exception";
                return false;
            }
        }

        static bool IsHex(string value)
        {
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (!((character >= '0' && character <= '9')
                    || (character >= 'a' && character <= 'f')
                    || (character >= 'A' && character <= 'F'))) return false;
            }
            return true;
        }

        static bool IsSafePackageAssetPath(string original, string normalized)
        {
            if (string.IsNullOrEmpty(original) || original[0] == '/' || original[0] == '\\'
                || string.IsNullOrEmpty(normalized) || !TryLocalPackageName(normalized, out _)) return false;
            var parts = normalized.Split('/');
            if (parts.Length < 3) return false;
            for (var index = 0; index < parts.Length; index++)
            {
                var part = parts[index];
                if (part.Length == 0 || part == "." || part == ".." || part.IndexOf(':') >= 0) return false;
                for (var character = 0; character < part.Length; character++) if (char.IsControl(part[character])) return false;
            }
            return true;
        }

        static bool IsMutableLocalSource(string source)
        {
            return string.Equals(source, PackageSource.Local.ToString(), StringComparison.Ordinal)
                || string.Equals(source, PackageSource.Embedded.ToString(), StringComparison.Ordinal);
        }

        static bool IsSafePackageName(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > MaxPackageNameChars)
            {
                return false;
            }
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (!((character >= 'a' && character <= 'z')
                    || (character >= '0' && character <= '9')
                    || character == '.' || character == '-' || character == '_'))
                {
                    return false;
                }
            }
            return true;
        }

        static bool TryLocalPackageName(string assetPath, out string packageName)
        {
            packageName = string.Empty;
            if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                return false;
            }
            var separator = assetPath.IndexOf('/', "Packages/".Length);
            if (separator <= "Packages/".Length)
            {
                return false;
            }
            packageName = assetPath.Substring("Packages/".Length, separator - "Packages/".Length);
            return IsSafePackageName(packageName);
        }

        static string NormalizeAssetPath(string value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty : value.Replace('\\', '/').TrimStart('/');
        }

        static bool ShouldTrack(string path)
        {
            return !string.IsNullOrEmpty(path) && CodeExtensions.Contains(Path.GetExtension(path));
        }

        static bool IsUnityIgnoredPackageDirectory(string path)
        {
            var name = Path.GetFileName(TrimTrailingSeparators(path));
            return !string.IsNullOrEmpty(name) && name.EndsWith("~", StringComparison.Ordinal);
        }

        static bool HasReparsePointInPath(string path)
        {
            var current = new DirectoryInfo(path);
            while (current != null)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
                current = current.Parent;
            }
            return false;
        }

        static bool IsNetworkDrive(string path)
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }

        static bool IsSameOrChildPath(string path, string root)
        {
            var candidate = TrimTrailingSeparators(Path.GetFullPath(path));
            var canonicalRoot = TrimTrailingSeparators(Path.GetFullPath(root));
            var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(candidate, canonicalRoot, comparison)
                || candidate.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, comparison);
        }

        static string TrimTrailingSeparators(string path)
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                ? root
                : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        static bool BudgetExceeded(Stopwatch budget, long maxScanMilliseconds)
        {
            return budget == null || maxScanMilliseconds < 0 || budget.ElapsedMilliseconds > maxScanMilliseconds;
        }

        static bool TrySha256Lines(IList<string> lines, Stopwatch budget, long maxScanMilliseconds, out string fingerprint)
        {
            fingerprint = string.Empty;
            using (var sha256 = SHA256.Create())
            {
                for (var index = 0; index < lines.Count; index++)
                {
                    if (BudgetExceeded(budget, maxScanMilliseconds))
                    {
                        return false;
                    }
                    if (index > 0)
                    {
                        sha256.TransformBlock(LineSeparator, 0, LineSeparator.Length, LineSeparator, 0);
                    }

                    var bytes = Encoding.UTF8.GetBytes(lines[index] ?? string.Empty);
                    sha256.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
                }
                sha256.TransformFinalBlock(new byte[0], 0, 0);
                if (BudgetExceeded(budget, maxScanMilliseconds))
                {
                    return false;
                }

                var hash = sha256.Hash;
                var builder = new StringBuilder(hash.Length * 2);
                for (var index = 0; index < hash.Length; index++)
                {
                    builder.Append(hash[index].ToString("x2"));
                }
                fingerprint = builder.ToString();
                return true;
            }
        }
    }

    internal sealed class LocalPackageCodeInventoryInput
    {
        public LocalPackageCodeInventoryInput(string source, string name, string resolvedPath)
        {
            Source = source ?? string.Empty;
            Name = name ?? string.Empty;
            ResolvedPath = resolvedPath ?? string.Empty;
        }

        public string Source { get; private set; }
        public string Name { get; private set; }
        public string ResolvedPath { get; private set; }
    }

    internal sealed class LocalPackageSourceMetadataEvidence
    {
        public LocalPackageSourceMetadataEvidence(string reason, int observedGuidLength)
        {
            Reason = reason ?? string.Empty;
            ObservedGuidLength = Math.Max(0, observedGuidLength);
        }

        public string Reason { get; private set; }
        public int ObservedGuidLength { get; private set; }
    }

    internal sealed class LocalPackageInventoryMismatchDetail
    {
        public LocalPackageInventoryMismatchDetail(string kind, string path, string metadataReason = "", int observedGuidLength = 0)
        {
            Kind = kind ?? string.Empty;
            Path = path ?? string.Empty;
            MetadataReason = metadataReason ?? string.Empty;
            ObservedGuidLength = Math.Max(0, observedGuidLength);
        }

        public string Kind { get; private set; }
        public string Path { get; private set; }
        public string MetadataReason { get; private set; }
        public int ObservedGuidLength { get; private set; }
    }

    internal sealed class LocalPackageCodeInventorySnapshot
    {
        const int MaxProtocolPackageCount = 64;
        const int MaxProtocolFileCount = 16384;
        const int MaxProtocolMismatchCount = MaxProtocolFileCount * 4;

        LocalPackageCodeInventorySnapshot() { }

        public string Status { get; private set; }
        public string FailureReason { get; private set; }
        public string MismatchPackage { get; private set; }
        public int LocalPackageCount { get; private set; }
        public int SourceFileCount { get; private set; }
        public int AssetDatabaseFileCount { get; private set; }
        public int MissingAssetFileCount { get; private set; }
        public int UnexpectedAssetFileCount { get; private set; }
        public int CompilerSourceFileCount { get; private set; }
        public int MissingCompilerSourceFileCount { get; private set; }
        public int UnexpectedCompilerSourceFileCount { get; private set; }
        public string SourceFingerprint { get; private set; }
        public string AssetDatabaseFingerprint { get; private set; }
        public string SourceStateFingerprint { get; private set; }
        public string CompilerSourceFingerprint { get; private set; }
        public LocalPackageInventoryMismatchDetail[] MismatchDetails { get; private set; }
        public int MismatchDetailTotalCount { get; private set; }
        public bool MismatchDetailsTruncated => MismatchDetails.Length < MismatchDetailTotalCount;
        public bool IsCurrent => string.Equals(Status, "current", StringComparison.Ordinal);
        public bool IsStale => string.Equals(Status, "stale", StringComparison.Ordinal);
        public bool IsUnavailable => string.Equals(Status, "unavailable", StringComparison.Ordinal);

        public static LocalPackageCodeInventorySnapshot Available(int localPackageCount, int sourceFileCount, int assetDatabaseFileCount, int missingAssetFileCount, int unexpectedAssetFileCount, string mismatchPackage, string sourceFingerprint, string assetDatabaseFingerprint, string sourceStateFingerprint, int compilerSourceFileCount = 0, int missingCompilerSourceFileCount = 0, int unexpectedCompilerSourceFileCount = 0, string compilerSourceFingerprint = "", LocalPackageInventoryMismatchDetail[] mismatchDetails = null, int mismatchDetailTotalCount = 0)
        {
            return new LocalPackageCodeInventorySnapshot
            {
                Status = missingAssetFileCount > 0 || unexpectedAssetFileCount > 0 || missingCompilerSourceFileCount > 0 || unexpectedCompilerSourceFileCount > 0 ? "stale" : "current",
                FailureReason = string.Empty,
                MismatchPackage = mismatchPackage ?? string.Empty,
                LocalPackageCount = ClampCount(localPackageCount, MaxProtocolPackageCount),
                SourceFileCount = ClampCount(sourceFileCount, MaxProtocolFileCount),
                AssetDatabaseFileCount = ClampCount(assetDatabaseFileCount, MaxProtocolFileCount),
                MissingAssetFileCount = ClampCount(missingAssetFileCount, MaxProtocolFileCount),
                UnexpectedAssetFileCount = ClampCount(unexpectedAssetFileCount, MaxProtocolFileCount),
                CompilerSourceFileCount = ClampCount(compilerSourceFileCount, MaxProtocolFileCount),
                MissingCompilerSourceFileCount = ClampCount(missingCompilerSourceFileCount, MaxProtocolFileCount),
                UnexpectedCompilerSourceFileCount = ClampCount(unexpectedCompilerSourceFileCount, MaxProtocolFileCount),
                SourceFingerprint = sourceFingerprint ?? string.Empty,
                AssetDatabaseFingerprint = assetDatabaseFingerprint ?? string.Empty,
                SourceStateFingerprint = sourceStateFingerprint ?? string.Empty,
                CompilerSourceFingerprint = compilerSourceFingerprint ?? string.Empty,
                MismatchDetails = mismatchDetails ?? new LocalPackageInventoryMismatchDetail[0],
                MismatchDetailTotalCount = ClampCount(mismatchDetailTotalCount, MaxProtocolMismatchCount),
            };
        }

        public static LocalPackageCodeInventorySnapshot Unavailable(string failureReason, int localPackageCount = 0, int sourceFileCount = 0, int assetDatabaseFileCount = 0)
        {
            return new LocalPackageCodeInventorySnapshot
            {
                Status = "unavailable",
                FailureReason = failureReason ?? "read_exception",
                MismatchPackage = string.Empty,
                LocalPackageCount = ClampCount(localPackageCount, MaxProtocolPackageCount),
                SourceFileCount = ClampCount(sourceFileCount, MaxProtocolFileCount),
                AssetDatabaseFileCount = ClampCount(assetDatabaseFileCount, MaxProtocolFileCount),
                SourceFingerprint = string.Empty,
                AssetDatabaseFingerprint = string.Empty,
                SourceStateFingerprint = string.Empty,
                CompilerSourceFingerprint = string.Empty,
                MismatchDetails = new LocalPackageInventoryMismatchDetail[0],
                MismatchDetailTotalCount = 0,
            };
        }

        static int ClampCount(int value, int maximum)
        {
            return Math.Min(maximum, Math.Max(0, value));
        }
    }
}
