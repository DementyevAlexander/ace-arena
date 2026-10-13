using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SGG.UnityMCP.Editor.Commands;
using SGG.UnityMCP.Editor.Setup;
using UnityEditor.PackageManager;

namespace SGG.UnityMCP.Editor.Bridge
{
    internal static class LocalPackageMetadataInspector
    {
        const int MaxManifestBytes = 64 * 1024;
        const int MaxTotalManifestBytes = 512 * 1024;
        const int MaxInspectedLocalPackages = 64;
        const int MaxFieldChars = 256;

        public static LocalPackageMetadataEvidence Inspect(PackageInfo package)
        {
            if (package == null)
            {
                return LocalPackageMetadataEvidence.Unavailable(string.Empty, string.Empty, "package_info_unavailable");
            }

            return Inspect(package.source.ToString(), package.resolvedPath, package.name, package.version, MaxManifestBytes);
        }

        internal static LocalPackageMetadataEvidence Inspect(string source, string resolvedPath, string unityName, string unityVersion)
        {
            return Inspect(source, resolvedPath, unityName, unityVersion, MaxManifestBytes);
        }

        static LocalPackageMetadataEvidence Inspect(string source, string resolvedPath, string unityName, string unityVersion, int remainingBytes)
        {
            if (!IsMutableLocalSource(source))
            {
                return LocalPackageMetadataEvidence.NotApplicable(unityName, unityVersion);
            }

            if (remainingBytes <= 0)
            {
                return LocalPackageMetadataEvidence.Unavailable(unityName, unityVersion, "inspection_budget_exceeded");
            }

            if (string.IsNullOrEmpty(resolvedPath))
            {
                return LocalPackageMetadataEvidence.Unavailable(unityName, unityVersion, "resolved_path_unavailable");
            }
            if (resolvedPath.StartsWith("\\\\", StringComparison.Ordinal) || resolvedPath.StartsWith("//", StringComparison.Ordinal))
            {
                return LocalPackageMetadataEvidence.Unavailable(unityName, unityVersion, "network_resolved_path_not_inspected");
            }

            var inspectedBytes = 0;
            try
            {
                var root = Path.GetFullPath(resolvedPath);
                var manifestPath = Path.GetFullPath(Path.Combine(root, "package.json"));
                if (IsNetworkDrive(root))
                {
                    return LocalPackageMetadataEvidence.Unavailable(unityName, unityVersion, "network_resolved_path_not_inspected");
                }
                if (HasReparsePointInPath(root))
                {
                    return LocalPackageMetadataEvidence.Unavailable(unityName, unityVersion, "local_manifest_reparse_point");
                }
                if (!IsSameOrChildPath(manifestPath, root) || !File.Exists(manifestPath))
                {
                    return LocalPackageMetadataEvidence.Unavailable(unityName, unityVersion, "local_manifest_missing");
                }

                if ((File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0)
                {
                    return LocalPackageMetadataEvidence.Unavailable(unityName, unityVersion, "local_manifest_reparse_point");
                }

                var fileInfo = new FileInfo(manifestPath);
                var readLimit = Math.Min(MaxManifestBytes, remainingBytes);
                if (fileInfo.Length <= 0 || fileInfo.Length > readLimit)
                {
                    var error = fileInfo.Length > MaxManifestBytes ? "local_manifest_size_invalid" : "inspection_budget_exceeded";
                    return LocalPackageMetadataEvidence.Unavailable(unityName, unityVersion, error);
                }

                string json;
                using (var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var bytes = new byte[readLimit + 1];
                    var total = 0;
                    while (total < bytes.Length)
                    {
                        var read = stream.Read(bytes, total, bytes.Length - total);
                        if (read <= 0) break;
                        total += read;
                        inspectedBytes = total;
                    }
                    if (total > readLimit)
                    {
                        var error = readLimit == MaxManifestBytes ? "local_manifest_size_invalid" : "inspection_budget_exceeded";
                        return LocalPackageMetadataEvidence.Unavailable(unityName, unityVersion, error, total);
                    }
                    json = new UTF8Encoding(false, true).GetString(bytes, 0, total);
                }

                if (!PipelineJsonEvidenceParser.TryReadStringPath(json, new[] { "name" }, out var manifestName)
                    || !PipelineJsonEvidenceParser.TryReadStringPath(json, new[] { "version" }, out var manifestVersion)
                    || !IsBoundedField(manifestName)
                    || !IsBoundedField(manifestVersion))
                {
                    return LocalPackageMetadataEvidence.Unavailable(unityName, unityVersion, "local_manifest_invalid", inspectedBytes);
                }

                return LocalPackageMetadataEvidence.Compared(unityName, unityVersion, manifestName, manifestVersion, inspectedBytes);
            }
            catch
            {
                return LocalPackageMetadataEvidence.Unavailable(unityName, unityVersion, "local_manifest_unreadable", inspectedBytes);
            }
        }

        public static LocalPackageMetadataSummary InspectRegisteredPackages(PackageInfo[] packages)
        {
            return InspectPackages(packages, false).Summary;
        }

        internal static LocalPackageMetadataInput[] CapturePackages(PackageInfo[] packages)
        {
            var count = packages == null ? 0 : packages.Length;
            var inputs = new LocalPackageMetadataInput[count];
            for (var index = 0; index < count; index++)
            {
                var package = packages[index];
                inputs[index] = package == null
                    ? null
                    : new LocalPackageMetadataInput(package.source.ToString(), package.resolvedPath, package.name, package.version);
            }
            return inputs;
        }

        internal static LocalPackageMetadataSummary InspectCapturedPackages(LocalPackageMetadataInput[] packages)
        {
            var localEvidence = new List<LocalPackageMetadataEvidence>();
            var inspectedLocalPackages = 0;
            var remainingBytes = MaxTotalManifestBytes;
            var count = packages == null ? 0 : packages.Length;
            for (var index = 0; index < count; index++)
            {
                var package = packages[index];
                LocalPackageMetadataEvidence item;
                if (package != null && IsMutableLocalSource(package.Source) && inspectedLocalPackages >= MaxInspectedLocalPackages)
                {
                    item = LocalPackageMetadataEvidence.Unavailable(package.Name, package.Version, "inspection_budget_exceeded");
                }
                else
                {
                    item = package == null
                        ? LocalPackageMetadataEvidence.Unavailable(string.Empty, string.Empty, "package_info_unavailable")
                        : Inspect(package.Source, package.ResolvedPath, package.Name, package.Version, remainingBytes);
                    if (item.IsLocal)
                    {
                        inspectedLocalPackages++;
                        remainingBytes = Math.Max(0, remainingBytes - item.InspectedBytes);
                    }
                }

                if (item.IsLocal)
                {
                    localEvidence.Add(item);
                }
            }

            localEvidence.Sort((left, right) => string.Compare(left.UnityReportedName, right.UnityReportedName, StringComparison.Ordinal));
            var inspectionTruncated = localEvidence.Exists(item => string.Equals(item.ErrorCode, "inspection_budget_exceeded", StringComparison.Ordinal));
            return LocalPackageMetadataSummary.FromEvidence(localEvidence, false, inspectionTruncated);
        }

        public static LocalPackageMetadataBatch InspectPackages(PackageInfo[] packages, bool inputTruncated)
        {
            var evidence = new LocalPackageMetadataEvidence[packages == null ? 0 : packages.Length];
            var localEvidence = new List<LocalPackageMetadataEvidence>();
            var inspectedLocalPackages = 0;
            var remainingBytes = MaxTotalManifestBytes;
            var count = packages == null ? 0 : packages.Length;
            for (var index = 0; index < count; index++)
            {
                var package = packages[index];
                LocalPackageMetadataEvidence item;
                if (package != null && IsMutableLocalSource(package.source.ToString()) && inspectedLocalPackages >= MaxInspectedLocalPackages)
                {
                    item = LocalPackageMetadataEvidence.Unavailable(package.name, package.version, "inspection_budget_exceeded");
                }
                else
                {
                    item = package == null
                        ? LocalPackageMetadataEvidence.Unavailable(string.Empty, string.Empty, "package_info_unavailable")
                        : Inspect(package.source.ToString(), package.resolvedPath, package.name, package.version, remainingBytes);
                    if (item.IsLocal)
                    {
                        inspectedLocalPackages++;
                        remainingBytes = Math.Max(0, remainingBytes - item.InspectedBytes);
                    }
                }

                evidence[index] = item;
                if (item.IsLocal)
                {
                    localEvidence.Add(item);
                }
            }

            localEvidence.Sort((left, right) => string.Compare(left.UnityReportedName, right.UnityReportedName, StringComparison.Ordinal));
            var inspectionTruncated = localEvidence.Exists(item => string.Equals(item.ErrorCode, "inspection_budget_exceeded", StringComparison.Ordinal));
            return new LocalPackageMetadataBatch(evidence, LocalPackageMetadataSummary.FromEvidence(localEvidence, inputTruncated, inspectionTruncated));
        }

        public static void AppendPackageJsonFields(StringBuilder builder, LocalPackageMetadataEvidence evidence)
        {
            builder.Append(",\"version_source\":\"unity_package_manager\"");
            if (evidence == null || !evidence.IsLocal)
            {
                return;
            }

            builder.Append(",\"metadata_status\":").Append(JsonString(evidence.Status));
            builder.Append(",\"unity_reported_version\":").Append(JsonString(evidence.UnityReportedVersion));
            builder.Append(",\"stale_package_resolution\":").Append(evidence.IsStale ? "true" : "false");
            if (!evidence.IsUnavailable)
            {
                builder.Append(",\"local_manifest_name\":").Append(JsonString(evidence.LocalManifestName));
                builder.Append(",\"local_manifest_version\":").Append(JsonString(evidence.LocalManifestVersion));
                builder.Append(",\"local_manifest_name_matches\":").Append(evidence.NameMatches ? "true" : "false");
                builder.Append(",\"local_manifest_version_matches\":").Append(evidence.VersionMatches ? "true" : "false");
            }
            if (!string.IsNullOrEmpty(evidence.ErrorCode))
            {
                builder.Append(",\"metadata_error\":").Append(JsonString(evidence.ErrorCode));
            }
        }

        public static void AppendSummaryJsonFields(StringBuilder builder, LocalPackageMetadataSummary summary)
        {
            summary = summary ?? LocalPackageMetadataSummary.FromEvidence(new List<LocalPackageMetadataEvidence>(), true, false);
            builder.Append(",\"local_metadata_status\":").Append(JsonString(summary.Status));
            builder.Append(",\"local_metadata_scope\":\"returned_packages\"");
            builder.Append(",\"local_metadata_checked_count\":").Append(summary.CheckedCount);
            builder.Append(",\"stale_local_metadata_count\":").Append(summary.StaleCount);
            builder.Append(",\"unavailable_local_metadata_count\":").Append(summary.UnavailableCount);
            builder.Append(",\"local_metadata_truncated\":").Append(summary.Truncated ? "true" : "false");
            builder.Append(",\"local_metadata_query_truncated\":").Append(summary.QueryTruncated ? "true" : "false");
            builder.Append(",\"local_metadata_inspection_truncated\":").Append(summary.InspectionTruncated ? "true" : "false");
        }

        static bool IsBoundedField(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length <= MaxFieldChars && value.IndexOf('\0') < 0;
        }

        static bool IsMutableLocalSource(string source)
        {
            return string.Equals(source, PackageSource.Local.ToString(), StringComparison.Ordinal)
                || string.Equals(source, PackageSource.Embedded.ToString(), StringComparison.Ordinal);
        }

        static bool IsSameOrChildPath(string path, string root)
        {
            var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), trimmedRoot, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(trimmedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(trimmedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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

        static string JsonString(string value)
        {
            return JsonUtil.String(value ?? string.Empty);
        }

    }

    internal sealed class LocalPackageMetadataInput
    {
        public LocalPackageMetadataInput(string source, string resolvedPath, string name, string version)
        {
            Source = source ?? string.Empty;
            ResolvedPath = resolvedPath ?? string.Empty;
            Name = name ?? string.Empty;
            Version = version ?? string.Empty;
        }

        public string Source { get; private set; }
        public string ResolvedPath { get; private set; }
        public string Name { get; private set; }
        public string Version { get; private set; }
    }

    internal sealed class LocalPackageMetadataEvidence
    {
        LocalPackageMetadataEvidence() { }

        public string Status { get; private set; }
        public string UnityReportedName { get; private set; }
        public string UnityReportedVersion { get; private set; }
        public string LocalManifestName { get; private set; }
        public string LocalManifestVersion { get; private set; }
        public string ErrorCode { get; private set; }
        public int InspectedBytes { get; private set; }
        public bool IsLocal { get; private set; }
        public bool NameMatches { get; private set; }
        public bool VersionMatches { get; private set; }
        public bool IsStale => IsLocal && string.Equals(Status, "stale_package_resolution", StringComparison.Ordinal);
        public bool IsUnavailable => IsLocal && string.Equals(Status, "unavailable", StringComparison.Ordinal);

        public static LocalPackageMetadataEvidence NotApplicable(string unityName, string unityVersion)
        {
            return Create("not_applicable", unityName, unityVersion, string.Empty, string.Empty, string.Empty, false, false, false);
        }

        public static LocalPackageMetadataEvidence Unavailable(string unityName, string unityVersion, string errorCode, int inspectedBytes = 0)
        {
            return Create("unavailable", unityName, unityVersion, string.Empty, string.Empty, errorCode, true, false, false, inspectedBytes);
        }

        public static LocalPackageMetadataEvidence Compared(string unityName, string unityVersion, string localName, string localVersion, int inspectedBytes)
        {
            var nameMatches = string.Equals(unityName, localName, StringComparison.Ordinal);
            var versionMatches = string.Equals(unityVersion, localVersion, StringComparison.Ordinal);
            return Create(nameMatches && versionMatches ? "current" : "stale_package_resolution", unityName, unityVersion, localName, localVersion, string.Empty, true, nameMatches, versionMatches, inspectedBytes);
        }

        static LocalPackageMetadataEvidence Create(string status, string unityName, string unityVersion, string localName, string localVersion, string errorCode, bool isLocal, bool nameMatches, bool versionMatches, int inspectedBytes = 0)
        {
            return new LocalPackageMetadataEvidence
            {
                Status = status,
                UnityReportedName = unityName ?? string.Empty,
                UnityReportedVersion = unityVersion ?? string.Empty,
                LocalManifestName = localName ?? string.Empty,
                LocalManifestVersion = localVersion ?? string.Empty,
                ErrorCode = errorCode ?? string.Empty,
                InspectedBytes = Math.Max(0, inspectedBytes),
                IsLocal = isLocal,
                NameMatches = nameMatches,
                VersionMatches = versionMatches,
            };
        }
    }

    internal sealed class LocalPackageMetadataBatch
    {
        public LocalPackageMetadataBatch(LocalPackageMetadataEvidence[] evidence, LocalPackageMetadataSummary summary)
        {
            Evidence = evidence ?? new LocalPackageMetadataEvidence[0];
            Summary = summary;
        }

        public LocalPackageMetadataEvidence[] Evidence { get; private set; }
        public LocalPackageMetadataSummary Summary { get; private set; }
    }

    internal sealed class LocalPackageMetadataSummary
    {
        LocalPackageMetadataSummary() { }

        public string Status { get; private set; }
        public int CheckedCount { get; private set; }
        public int StaleCount { get; private set; }
        public int UnavailableCount { get; private set; }
        public bool Truncated { get; private set; }
        public bool QueryTruncated { get; private set; }
        public bool InspectionTruncated { get; private set; }

        public static LocalPackageMetadataSummary FromEvidence(IList<LocalPackageMetadataEvidence> evidence, bool queryTruncated, bool inspectionTruncated)
        {
            var stale = 0;
            var unavailable = 0;
            var count = evidence == null ? 0 : evidence.Count;
            for (var index = 0; index < count; index++)
            {
                if (evidence[index].IsStale) stale++;
                if (evidence[index].IsUnavailable) unavailable++;
            }

            return new LocalPackageMetadataSummary
            {
                Status = stale > 0 ? "stale_package_resolution" : unavailable > 0 || queryTruncated || inspectionTruncated ? "unavailable" : "current",
                CheckedCount = count,
                StaleCount = stale,
                UnavailableCount = unavailable,
                Truncated = queryTruncated || inspectionTruncated,
                QueryTruncated = queryTruncated,
                InspectionTruncated = inspectionTruncated,
            };
        }
    }
}
