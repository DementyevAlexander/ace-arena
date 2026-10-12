using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using SGG.UnityMCP.Editor.Bridge;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class PackageManagementCommands
    {
        const string Capability = "package_management";
        const string InstallSchemaJson = "{\"type\":\"object\",\"properties\":{\"package_path\":{\"type\":\"string\"},\"expected_name\":{\"type\":\"string\"}},\"required\":[\"package_path\"],\"additionalProperties\":false}";
        const string InstallGitSchemaJson = "{\"type\":\"object\",\"properties\":{\"git_url\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":1024},\"expected_name\":{\"type\":\"string\"}},\"required\":[\"git_url\"],\"additionalProperties\":false}";
        const string InfoSchemaJson = "{\"type\":\"object\",\"properties\":{\"package_name\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128},\"include_samples\":{\"type\":\"boolean\"},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}},\"additionalProperties\":false}";
        const string InfoOutputLimitJson = "{\"max_results\":200,\"max_samples_per_package\":25,\"max_description_chars\":1000}";
        const string RemoveSchemaJson = "{\"type\":\"object\",\"properties\":{\"package_name\":{\"type\":\"string\"}},\"required\":[\"package_name\"],\"additionalProperties\":false}";
        const string SearchSchemaJson = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"minLength\":2,\"maxLength\":128},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100}},\"required\":[\"query\"],\"additionalProperties\":false}";
        const string UpdateSchemaJson = "{\"type\":\"object\",\"properties\":{\"package_name\":{\"type\":\"string\"},\"version\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128}},\"required\":[\"package_name\",\"version\"],\"additionalProperties\":false}";
        const string OperationStatusSchemaJson = "{\"type\":\"object\",\"properties\":{\"operation_id\":{\"type\":\"string\"}},\"required\":[\"operation_id\"],\"additionalProperties\":false}";
        const string OperationListSchemaJson = "{\"type\":\"object\",\"properties\":{\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100},\"include_completed\":{\"type\":\"boolean\"},\"include_running\":{\"type\":\"boolean\"}},\"additionalProperties\":false}";
        const string OperationPruneSchemaJson = "{\"type\":\"object\",\"properties\":{\"older_than_days\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":365},\"max_delete\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"older_than_days\",\"max_delete\",\"dry_run\"],\"additionalProperties\":false}";
        const string ResultShape = "package_operation";
        const int MaxPathLength = 512;
        const int MaxPackageSpecLength = 1024;
        const int DefaultInfoMaxResults = 50;
        const int HardInfoMaxResults = 200;
        const int MaxPackageInfoQueryLength = 128;
        const int MaxSamplesPerPackage = 25;
        const int MaxDescriptionChars = 1000;
        const int DefaultSearchMaxResults = 20;
        const int HardSearchMaxResults = 100;
        const int DefaultOperationMaxResults = 20;
        const int HardOperationMaxResults = 100;
        const int PackageOperationLongRunningMs = 10 * 60 * 1000;
        const int PackageOperationStalePersistedMs = 2 * 60 * 1000;
        const int UpmLogTailBytes = 64 * 1024;
        const int UpmEvidenceMaxChars = 2000;

        static readonly object OperationsLock = new object();
        static readonly Dictionary<string, PackageOperation> Operations = new Dictionary<string, PackageOperation>(StringComparer.Ordinal);
        static readonly Regex UpmRenameEpermRegex = new Regex(@"(?<package>[A-Za-z0-9_.-]+):\s*EPERM:\s*operation not permitted,\s*rename\s*'(?<source>[^']+)'\s*->\s*'(?<target>[^']+)'", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        static readonly Regex UpmFailedRenameEpermRegex = new Regex(@"Failed to rename\s*\[(?<source>[^\]]+)\]\s*to\s*\[(?<target>[^\]]+)\].*error code\s*\[EPERM\]", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        [McpCommand(
            "package.info",
            Title = "Package info",
            Description = "Read installed Unity package metadata and sample status without registry/network access",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = InfoSchemaJson,
            Tags = new string[] { "package", "unity-ai-assistant" },
            Capability = Capability,
            ResultShape = "package_info",
            OutputLimitJson = InfoOutputLimitJson)]
        public static CommandExecutionResult PackageInfo(McpCommandContext context)
        {
            var validation = RequirePackageInfoQuery(context.ArgsJson, out var packageName, out var maxResults, out var includeSamples);
            if (validation != null)
            {
                return validation;
            }

            return CommandExecutionResult.Ok(PackageInfoListJson(packageName, maxResults, includeSamples));
        }

        [McpCommand(
            "package.install",
            Title = "Install local package",
            Description = "Start installing a local UPM package folder by path relative to Packages/manifest.json",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 10000,
            SchemaJson = InstallSchemaJson,
            Tags = new string[] { "package" },
            Capability = Capability,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = ResultShape)]
        public static CommandExecutionResult PackageInstall(McpCommandContext context)
        {
            var statusResult = RequireEditingStatus();
            if (statusResult != null)
            {
                return statusResult;
            }

            var validation = RequireLocalPackageSpec(context.ArgsJson, out var packageSpec, out var packageName);
            if (validation != null)
            {
                return validation;
            }

            var request = Client.Add(packageSpec);
            var operation = RegisterOperation("install", packageName, packageSpec, request, () => PackageInfoJson(request.Result));
            return CommandExecutionResult.Ok(operation.ToJson());
        }

        [McpCommand(
            "package.install.git",
            Title = "Install Git package",
            Description = "Start installing a UPM package from an explicit HTTPS Git URL",
            Risk = "network",
            Idempotency = "idempotent",
            TimeoutMs = 10000,
            SchemaJson = InstallGitSchemaJson,
            Tags = new string[] { "package" },
            Capability = Capability,
            RequiresEditorStatus = new string[] { "editing" },
            FeatureFlag = "package_registry_network",
            ResultShape = ResultShape)]
        public static CommandExecutionResult PackageInstallGit(McpCommandContext context)
        {
            var statusResult = RequireEditingStatus();
            if (statusResult != null)
            {
                return statusResult;
            }

            var validation = RequireHttpsGitPackageSpec(context.ArgsJson, out var packageSpec, out var packageName);
            if (validation != null)
            {
                return validation;
            }

            var request = Client.Add(packageSpec);
            var operation = RegisterOperation("install_git", packageName, packageSpec, request, () => PackageInfoJson(request.Result));
            return CommandExecutionResult.Ok(operation.ToJson());
        }

        [McpCommand(
            "package.remove",
            Title = "Remove package",
            Description = "Start removing an installed Unity package by package name",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 10000,
            SchemaJson = RemoveSchemaJson,
            Tags = new string[] { "package" },
            Capability = Capability,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = ResultShape)]
        public static CommandExecutionResult PackageRemove(McpCommandContext context)
        {
            var statusResult = RequireEditingStatus();
            if (statusResult != null)
            {
                return statusResult;
            }

            var packageName = JsonUtil.ExtractString(context.ArgsJson, "package_name");
            if (string.IsNullOrEmpty(packageName))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument package_name is required");
            }

            if (!IsSafePackageName(packageName))
            {
                return CommandExecutionResult.Error("invalid_field_value", "package_name is not a valid package name");
            }

            if (!IsPackageInstalled(packageName))
            {
                return CommandExecutionResult.Error("package_not_found", "Package is not installed");
            }

            var request = Client.Remove(packageName);
            var operation = RegisterOperation("remove", packageName, packageName, request, () => "{\"removed\":true,\"package_name\":" + JsonUtil.String(packageName) + "}");
            return CommandExecutionResult.Ok(operation.ToJson());
        }

        [McpCommand(
            "package.search",
            Title = "Search package registry",
            Description = "Start a bounded Unity PackageManager registry search",
            Risk = "network",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = SearchSchemaJson,
            Tags = new string[] { "package" },
            Capability = Capability,
            FeatureFlag = "package_registry_network",
            ResultShape = ResultShape)]
        public static CommandExecutionResult PackageSearch(McpCommandContext context)
        {
            var validation = RequireRegistrySearchQuery(context.ArgsJson, out var query, out var maxResults);
            if (validation != null)
            {
                return validation;
            }

            var request = Client.Search(query);
            var operation = RegisterOperation("search", query, query, request, () => SearchResultsJson(request.Result, maxResults));
            return CommandExecutionResult.Ok(operation.ToJson());
        }

        [McpCommand(
            "package.update",
            Title = "Update package",
            Description = "Start updating an installed registry package to an explicit version",
            Risk = "network",
            Idempotency = "idempotent",
            TimeoutMs = 10000,
            SchemaJson = UpdateSchemaJson,
            Tags = new string[] { "package" },
            Capability = Capability,
            RequiresEditorStatus = new string[] { "editing" },
            FeatureFlag = "package_registry_network",
            ResultShape = ResultShape)]
        public static CommandExecutionResult PackageUpdate(McpCommandContext context)
        {
            var statusResult = RequireEditingStatus();
            if (statusResult != null)
            {
                return statusResult;
            }

            var validation = RequireRegistryPackageVersion(context.ArgsJson, out var packageName, out var version, out var packageSpec);
            if (validation != null)
            {
                return validation;
            }

            if (!TryGetInstalledPackage(packageName, out var installedPackage))
            {
                return CommandExecutionResult.Error("package_not_found", "Package is not installed");
            }

            if (!string.Equals(installedPackage.source.ToString(), "Registry", StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("invalid_field_value", "package_name must be an installed registry package");
            }

            var request = Client.Add(packageSpec);
            var operation = RegisterOperation("update", packageName, packageSpec, request, () => PackageInfoJson(request.Result));
            return CommandExecutionResult.Ok(operation.ToJson());
        }

        [McpCommand(
            "package.operation.status",
            Title = "Package operation status",
            Description = "Read status for an async Unity PackageManager operation",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = OperationStatusSchemaJson,
            Tags = new string[] { "package" },
            Capability = Capability,
            ResultShape = ResultShape)]
        public static CommandExecutionResult PackageOperationStatus(McpCommandContext context)
        {
            var operationId = JsonUtil.ExtractString(context.ArgsJson, "operation_id");
            if (string.IsNullOrEmpty(operationId))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument operation_id is required");
            }

            lock (OperationsLock)
            {
                if (!IsSafeOperationId(operationId))
                {
                    return CommandExecutionResult.Error("invalid_field_value", "operation_id is not valid");
                }

                if (!Operations.TryGetValue(operationId, out var operation))
                {
                    if (TryReadPersistedOperation(operationId, out var persistedJson))
                    {
                        var file = new FileInfo(OperationFilePath(operationId));
                        return CommandExecutionResult.Ok(PersistedOperationStatusJson(persistedJson, file.CreationTimeUtc, file.LastWriteTimeUtc));
                    }

                    return CommandExecutionResult.Error("package_operation_not_found", "Package operation was not found");
                }

                var json = operation.ToJson();
                WritePersistedOperation(operation.Id, json);
                return CommandExecutionResult.Ok(json);
            }
        }

        [McpCommand(
            "package.operation.list",
            Title = "List package operations",
            Description = "List bounded in-memory and persisted Unity PackageManager operations from fixed project-local storage",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = OperationListSchemaJson,
            Tags = new string[] { "package" },
            Capability = Capability,
            ResultShape = ResultShape)]
        public static CommandExecutionResult PackageOperationList(McpCommandContext context)
        {
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", DefaultOperationMaxResults), 1, HardOperationMaxResults);
            var includeCompleted = JsonUtil.ExtractBool(context.ArgsJson, "include_completed", true);
            var includeRunning = JsonUtil.ExtractBool(context.ArgsJson, "include_running", true);

            lock (OperationsLock)
            {
                var summaries = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pair in Operations)
                {
                    var operation = pair.Value;
                    var isCompleted = operation.Request.IsCompleted;
                    if ((isCompleted && !includeCompleted) || (!isCompleted && !includeRunning))
                    {
                        continue;
                    }

                    summaries.Add(OperationSummaryJson(operation.Id, operation.Kind, OperationStatus(operation), operation.PackageName, operation.StartedUtc, DateTime.UtcNow, "memory", operation.Request.IsCompleted));
                    seen.Add(operation.Id);
                }

                AddPersistedOperationSummaries(summaries, seen, includeCompleted, includeRunning);
                summaries.Sort(StringComparer.Ordinal);
                return CommandExecutionResult.Ok(OperationListJson(summaries, maxResults));
            }
        }

        [McpCommand(
            "package.operation.prune",
            Title = "Prune package operation records",
            Description = "Delete completed persisted Unity PackageManager operation records under fixed project-local storage",
            Risk = "destructive",
            Idempotency = "unsafe",
            TimeoutMs = 10000,
            SchemaJson = OperationPruneSchemaJson,
            Tags = new string[] { "package" },
            Capability = Capability,
            ResultShape = ResultShape)]
        public static CommandExecutionResult PackageOperationPrune(McpCommandContext context)
        {
            var olderThanDays = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "older_than_days", 0), 0, 365);
            var maxDelete = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_delete", 1), 1, HardOperationMaxResults);
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);

            lock (OperationsLock)
            {
                var runningIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pair in Operations)
                {
                    if (!pair.Value.Request.IsCompleted)
                    {
                        runningIds.Add(pair.Key);
                    }
                }

                var candidates = CompletedPersistedOperationFiles(DateTime.UtcNow.AddDays(-olderThanDays), maxDelete, runningIds);
                var deleted = 0;
                if (!dryRun)
                {
                    for (var index = 0; index < candidates.Count; index++)
                    {
                        try
                        {
                            File.Delete(candidates[index].FullName);
                            deleted++;
                        }
                        catch
                        {
                            // Prune is best-effort and only targets completed fixed-root records.
                        }
                    }
                }

                return CommandExecutionResult.Ok("{\"dry_run\":" + JsonUtil.Bool(dryRun) + ",\"older_than_days\":" + olderThanDays + ",\"max_delete\":" + maxDelete + ",\"would_delete\":" + candidates.Count + ",\"deleted\":" + deleted + "}");
            }
        }

        static CommandExecutionResult RequireEditingStatus()
        {
            if (EditorApplication.isCompiling)
            {
                return CommandExecutionResult.Error("unity_compiling", "Unity is compiling");
            }

            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return CommandExecutionResult.Error("unity_busy", "Package operations require editing status");
            }

            return null;
        }

        static CommandExecutionResult RequirePackageInfoQuery(string argsJson, out string packageName, out int maxResults, out bool includeSamples)
        {
            packageName = string.Empty;
            maxResults = Clamp(JsonUtil.ExtractInt(argsJson, "max_results", DefaultInfoMaxResults), 1, HardInfoMaxResults);
            includeSamples = JsonUtil.ExtractBool(argsJson, "include_samples", true);

            var rawPackageName = JsonUtil.ExtractString(argsJson, "package_name");
            if (string.IsNullOrEmpty(rawPackageName))
            {
                return null;
            }

            packageName = rawPackageName.Trim();
            if (packageName.Length < 1 || packageName.Length > MaxPackageInfoQueryLength)
            {
                return CommandExecutionResult.Error("invalid_field_value", "package_name must be 1..128 characters");
            }

            if (!IsSafePackageInfoQuery(packageName))
            {
                return CommandExecutionResult.Error("invalid_field_value", "package_name must be a package id/name substring, not a URL, path, version spec, or git spec");
            }

            return null;
        }

        static CommandExecutionResult RequireRegistrySearchQuery(string argsJson, out string query, out int maxResults)
        {
            query = string.Empty;
            maxResults = Clamp(JsonUtil.ExtractInt(argsJson, "max_results", DefaultSearchMaxResults), 1, HardSearchMaxResults);

            var rawQuery = JsonUtil.ExtractString(argsJson, "query");
            if (string.IsNullOrEmpty(rawQuery))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument query is required");
            }

            query = rawQuery.Trim();
            if (query.Length < 2 || query.Length > 128)
            {
                return CommandExecutionResult.Error("invalid_field_value", "query must be 2..128 characters");
            }

            if (!IsSafeRegistrySearchQuery(query))
            {
                return CommandExecutionResult.Error("invalid_field_value", "query must be a package name or bounded registry search term, not a URL, path, or git spec");
            }

            return null;
        }

        static CommandExecutionResult RequireLocalPackageSpec(string argsJson, out string packageSpec, out string packageName)
        {
            packageSpec = string.Empty;
            packageName = string.Empty;

            var packagePath = JsonUtil.ExtractString(argsJson, "package_path");
            if (string.IsNullOrEmpty(packagePath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument package_path is required");
            }

            if (packagePath.Length > MaxPathLength)
            {
                return CommandExecutionResult.Error("invalid_field_value", "package_path is too long");
            }

            if (packagePath.IndexOf('\0') >= 0)
            {
                return CommandExecutionResult.Error("path_not_allowed", "package_path cannot contain NUL");
            }

            var normalized = NormalizePackagePath(packagePath);
            if (string.IsNullOrEmpty(normalized))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument package_path is required");
            }

            if (normalized.StartsWith("/", StringComparison.Ordinal) || IsDriveAbsolute(normalized))
            {
                return CommandExecutionResult.Error("absolute_path_rejected", "package_path must be relative to Packages/manifest.json");
            }

            if (normalized.IndexOf(':') >= 0)
            {
                return CommandExecutionResult.Error("invalid_field_value", "Only local relative package paths are supported in this version");
            }

            if (HasTraversal(normalized))
            {
                return CommandExecutionResult.Error("path_traversal_rejected", "package_path must stay under Packages/");
            }

            var manifestDirectory = Path.Combine(ProjectPaths.ProjectRoot, "Packages");
            string resolvedPath;
            try
            {
                resolvedPath = Path.GetFullPath(Path.Combine(manifestDirectory, normalized));
            }
            catch (Exception exception)
            {
                return CommandExecutionResult.Error("invalid_field_value", "package_path could not be resolved: " + exception.Message);
            }

            if (!Directory.Exists(resolvedPath))
            {
                return CommandExecutionResult.Error("package_not_found", "package_path does not resolve to a package folder");
            }

            var packageJsonPath = Path.Combine(resolvedPath, "package.json");
            if (!File.Exists(packageJsonPath))
            {
                return CommandExecutionResult.Error("package_not_found", "package_path must contain package.json");
            }

            var manifestJson = File.ReadAllText(packageJsonPath);
            packageName = JsonUtil.ExtractString(manifestJson, "name");
            if (string.IsNullOrEmpty(packageName) || !IsSafePackageName(packageName))
            {
                return CommandExecutionResult.Error("invalid_field_value", "package.json has an invalid package name");
            }

            var expectedName = JsonUtil.ExtractString(argsJson, "expected_name");
            if (!string.IsNullOrEmpty(expectedName) && !string.Equals(expectedName, packageName, StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("invalid_field_value", "expected_name does not match package.json name");
            }

            packageSpec = "file:" + normalized;
            return null;
        }

        static CommandExecutionResult RequireHttpsGitPackageSpec(string argsJson, out string packageSpec, out string packageName)
        {
            packageSpec = string.Empty;
            packageName = string.Empty;

            var rawUrl = JsonUtil.ExtractString(argsJson, "git_url");
            if (string.IsNullOrEmpty(rawUrl))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument git_url is required");
            }

            packageSpec = rawUrl.Trim();
            if (packageSpec.Length < 1 || packageSpec.Length > MaxPackageSpecLength || HasUnsafeSpecCharacters(packageSpec))
            {
                return CommandExecutionResult.Error("invalid_field_value", "git_url must be a bounded HTTPS Git URL without whitespace or control characters");
            }

            if (packageSpec.IndexOf('\\') >= 0 || packageSpec.StartsWith("/", StringComparison.Ordinal) || IsDriveAbsolute(packageSpec))
            {
                return CommandExecutionResult.Error("invalid_field_value", "git_url must be an HTTPS Git URL, not a filesystem path");
            }

            var uriCandidate = packageSpec.StartsWith("git+https://", StringComparison.Ordinal) ? packageSpec.Substring(4) : packageSpec;
            if (!uriCandidate.StartsWith("https://", StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("invalid_field_value", "git_url must start with https:// or git+https://");
            }

            if (!Uri.TryCreate(uriCandidate, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            {
                return CommandExecutionResult.Error("invalid_field_value", "git_url must be an HTTPS Git URL without embedded credentials");
            }

            if (!IsSafeGitPackageUri(uri))
            {
                return CommandExecutionResult.Error("invalid_field_value", "git_url must point to a .git repository with optional ?path=/... and #revision");
            }

            var expectedName = JsonUtil.ExtractString(argsJson, "expected_name");
            if (!string.IsNullOrEmpty(expectedName))
            {
                if (!IsSafePackageName(expectedName))
                {
                    return CommandExecutionResult.Error("invalid_field_value", "expected_name is not a valid package name");
                }

                packageName = expectedName;
            }
            else
            {
                packageName = GitPackageNameFromUri(uri);
            }

            return null;
        }

        static CommandExecutionResult RequireRegistryPackageVersion(string argsJson, out string packageName, out string version, out string packageSpec)
        {
            packageName = JsonUtil.ExtractString(argsJson, "package_name");
            version = JsonUtil.ExtractString(argsJson, "version");
            packageSpec = string.Empty;

            if (string.IsNullOrEmpty(packageName))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument package_name is required");
            }

            if (!IsSafePackageName(packageName))
            {
                return CommandExecutionResult.Error("invalid_field_value", "package_name is not a valid package name");
            }

            if (string.IsNullOrEmpty(version))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument version is required");
            }

            version = version.Trim();
            if (version.Length < 1 || version.Length > 128 || !IsSafePackageVersion(version))
            {
                return CommandExecutionResult.Error("invalid_field_value", "version must be an explicit registry version, not a URL, path, or git spec");
            }

            packageSpec = packageName + "@" + version;
            return null;
        }

        static PackageOperation RegisterOperation(string kind, string packageName, string packageSpec, Request request, Func<string> resultJson)
        {
            lock (OperationsLock)
            {
                PruneCompletedOperations();
                var operation = new PackageOperation(NewOperationId(), kind, packageName, packageSpec, request, resultJson);
                Operations[operation.Id] = operation;
                WritePersistedOperation(operation.Id, operation.ToJson());
                return operation;
            }
        }

        static void PruneCompletedOperations()
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-15);
            var expired = new List<string>();
            foreach (var pair in Operations)
            {
                if (pair.Value.Request.IsCompleted && pair.Value.StartedUtc < cutoff)
                {
                    expired.Add(pair.Key);
                }
            }

            for (var index = 0; index < expired.Count; index++)
            {
                Operations.Remove(expired[index]);
            }
        }

        static bool IsPackageInstalled(string packageName)
        {
            return TryGetInstalledPackage(packageName, out _);
        }

        static bool TryGetInstalledPackage(string packageName, out PackageManagerPackageInfo package)
        {
            var packages = PackageManagerPackageInfo.GetAllRegisteredPackages();
            for (var index = 0; index < packages.Length; index++)
            {
                if (string.Equals(packages[index].name, packageName, StringComparison.Ordinal))
                {
                    package = packages[index];
                    return true;
                }
            }

            package = null;
            return false;
        }

        static string NormalizePackagePath(string packagePath)
        {
            var normalized = packagePath.Replace('\\', '/').Trim();
            while (normalized.Contains("//"))
            {
                normalized = normalized.Replace("//", "/");
            }

            return normalized;
        }

        static bool IsDriveAbsolute(string input)
        {
            return input.Length >= 2 && char.IsLetter(input[0]) && input[1] == ':';
        }

        static bool HasTraversal(string input)
        {
            foreach (var part in input.Split('/'))
            {
                if (part == "..")
                {
                    return true;
                }
            }

            return false;
        }

        static bool HasUnsafeSpecCharacters(string value)
        {
            for (var index = 0; index < value.Length; index++)
            {
                if (char.IsControl(value[index]) || char.IsWhiteSpace(value[index]))
                {
                    return true;
                }
            }

            return false;
        }

        static bool IsSafeGitPackageUri(Uri uri)
        {
            if (!uri.AbsolutePath.EndsWith(".git", StringComparison.OrdinalIgnoreCase) || HasUnsafeUrlPath(uri.AbsolutePath))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(uri.Query) && !IsSafeGitPathQuery(uri.Query))
            {
                return false;
            }

            return string.IsNullOrEmpty(uri.Fragment) || IsSafeGitRevision(uri.Fragment.Substring(1));
        }

        static bool HasUnsafeUrlPath(string value)
        {
            var parts = value.Split('/');
            foreach (var part in parts)
            {
                if (part == "." || part == ".." || part.IndexOf('\\') >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        static bool IsSafeGitPathQuery(string query)
        {
            if (!query.StartsWith("?path=/", StringComparison.Ordinal) || query.IndexOf('&') >= 0)
            {
                return false;
            }

            var packagePath = query.Substring("?path=".Length);
            return packagePath.Length > 1 && packagePath.Length <= 256 && packagePath.IndexOf('\\') < 0 && packagePath.IndexOf("..", StringComparison.Ordinal) < 0;
        }

        static bool IsSafeGitRevision(string revision)
        {
            if (revision.Length < 1 || revision.Length > 128 || revision.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                return false;
            }

            for (var index = 0; index < revision.Length; index++)
            {
                var character = revision[index];
                if (!char.IsLetterOrDigit(character) && character != '.' && character != '-' && character != '_' && character != '/' && character != '+')
                {
                    return false;
                }
            }

            return true;
        }

        static string GitPackageNameFromUri(Uri uri)
        {
            var pathPart = uri.AbsolutePath.TrimEnd('/');
            var slash = pathPart.LastIndexOf('/');
            var repoName = slash >= 0 ? pathPart.Substring(slash + 1) : pathPart;
            return repoName.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? repoName.Substring(0, repoName.Length - 4) : repoName;
        }

        static bool IsSafePackageName(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 214)
            {
                return false;
            }

            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (character == '\0' || character == '/' || character == '\\' || char.IsWhiteSpace(character))
                {
                    return false;
                }

                if (!char.IsLetterOrDigit(character) && character != '.' && character != '-' && character != '_')
                {
                    return false;
                }
            }

            return true;
        }

        static bool IsSafeRegistrySearchQuery(string value)
        {
            var lower = value.ToLowerInvariant();
            if (lower.Contains("://") || lower.StartsWith("git+", StringComparison.Ordinal) || lower.EndsWith(".git", StringComparison.Ordinal))
            {
                return false;
            }

            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (char.IsControl(character) || character == '/' || character == '\\' || character == ':' || character == '@' || character == '#')
                {
                    return false;
                }

                if (!char.IsLetterOrDigit(character) && character != '.' && character != '-' && character != '_' && character != ' ')
                {
                    return false;
                }
            }

            return true;
        }

        static bool IsSafePackageInfoQuery(string value)
        {
            var lower = value.ToLowerInvariant();
            if (lower.Contains("://") || lower.StartsWith("git+", StringComparison.Ordinal) || lower.EndsWith(".git", StringComparison.Ordinal))
            {
                return false;
            }

            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (char.IsControl(character) || char.IsWhiteSpace(character) || character == '/' || character == '\\' || character == ':' || character == '@' || character == '#')
                {
                    return false;
                }

                if (!char.IsLetterOrDigit(character) && character != '.' && character != '-' && character != '_')
                {
                    return false;
                }
            }

            return true;
        }

        static bool IsSafePackageVersion(string value)
        {
            var lower = value.ToLowerInvariant();
            if (lower.Contains("://") || lower.StartsWith("git+", StringComparison.Ordinal) || lower.EndsWith(".git", StringComparison.Ordinal))
            {
                return false;
            }

            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (char.IsControl(character) || char.IsWhiteSpace(character) || character == '/' || character == '\\' || character == ':' || character == '@' || character == '#')
                {
                    return false;
                }

                if (!char.IsLetterOrDigit(character) && character != '.' && character != '-' && character != '_' && character != '+')
                {
                    return false;
                }
            }

            return true;
        }

        static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            if (value > max)
            {
                return max;
            }

            return value;
        }

        static bool IsSafeOperationId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64)
            {
                return false;
            }

            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (!char.IsLetterOrDigit(character) && character != '-' && character != '_')
                {
                    return false;
                }
            }

            return true;
        }

        static string NewOperationId()
        {
            return "package-" + Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        static string PersistedOperationStatusJson(string persistedJson, DateTime createdUtc, DateTime updatedUtc)
        {
            var kind = JsonUtil.ExtractString(persistedJson, "kind");
            var packageName = JsonUtil.ExtractString(persistedJson, "package_name");
            var packageSpec = JsonUtil.ExtractString(persistedJson, "package_spec");
            var operationId = JsonUtil.ExtractString(persistedJson, "operation_id");
            var persistedStatus = JsonUtil.ExtractString(persistedJson, "status");
            var persistedCompleted = JsonUtil.ExtractBool(persistedJson, "is_completed", false);

            if (persistedCompleted && (string.Equals(persistedStatus, "succeeded", StringComparison.Ordinal) || string.Equals(persistedStatus, "failed", StringComparison.Ordinal)))
            {
                return persistedJson;
            }

            var upmRenameFailure = FindUpmPackageCacheRenameFailure();
            if (!persistedCompleted && upmRenameFailure != null && UpmFailureMatchesOperation(upmRenameFailure, packageName, packageSpec))
            {
                return PersistedOperationJson(operationId, kind, packageName, packageSpec, "failed", true, string.Empty, UpmFailureMessage(upmRenameFailure), createdUtc, updatedUtc, "persisted", false);
            }

            if (IsInstallCompletionKind(kind) && TryGetInstalledPackage(packageName, out var installedPackage) && InstalledPackageSatisfiesOperation(kind, packageSpec, installedPackage))
            {
                return PersistedOperationJson(operationId, kind, packageName, packageSpec, "succeeded", true, PackageInfoJson(installedPackage), string.Empty, createdUtc, updatedUtc, "persisted", true);
            }

            if (string.Equals(kind, "remove", StringComparison.Ordinal) && !string.IsNullOrEmpty(packageName) && !IsPackageInstalled(packageName))
            {
                return PersistedOperationJson(operationId, kind, packageName, packageSpec, "succeeded", true, "{\"removed\":true,\"package_name\":" + JsonUtil.String(packageName) + "}", string.Empty, createdUtc, updatedUtc, "persisted", true);
            }

            if (string.IsNullOrEmpty(persistedStatus))
            {
                persistedStatus = "running";
            }

            return PersistedOperationJson(operationId, kind, packageName, packageSpec, persistedStatus, false, string.Empty, string.Empty, createdUtc, updatedUtc, "persisted", false);
        }

        static void AddPersistedOperationSummaries(List<string> summaries, HashSet<string> seen, bool includeCompleted, bool includeRunning)
        {
            var files = SafeOperationFiles();
            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                var operationId = Path.GetFileNameWithoutExtension(file.Name);
                if (seen.Contains(operationId))
                {
                    continue;
                }

                string json;
                if (!TryReadSafeFile(file, out json))
                {
                    continue;
                }

                var statusJson = PersistedOperationStatusJson(json, file.CreationTimeUtc, file.LastWriteTimeUtc);
                var status = JsonUtil.ExtractString(statusJson, "status");
                var isCompleted = JsonUtil.ExtractBool(statusJson, "is_completed", false);
                if (string.IsNullOrEmpty(status))
                {
                    status = isCompleted ? "succeeded" : "running";
                }

                if ((isCompleted && !includeCompleted) || (!isCompleted && !includeRunning))
                {
                    continue;
                }

                summaries.Add(OperationSummaryJson(operationId, JsonUtil.ExtractString(statusJson, "kind"), status, JsonUtil.ExtractString(statusJson, "package_name"), file.CreationTimeUtc, file.LastWriteTimeUtc, "persisted", isCompleted));
                seen.Add(operationId);
            }
        }

        static List<FileInfo> CompletedPersistedOperationFiles(DateTime cutoffUtc, int maxDelete, HashSet<string> runningIds)
        {
            var candidates = new List<FileInfo>();
            var files = SafeOperationFiles();
            files.Sort((left, right) => left.LastWriteTimeUtc.CompareTo(right.LastWriteTimeUtc));
            for (var index = 0; index < files.Count && candidates.Count < maxDelete; index++)
            {
                var file = files[index];
                var operationId = Path.GetFileNameWithoutExtension(file.Name);
                if (runningIds.Contains(operationId) || file.LastWriteTimeUtc > cutoffUtc)
                {
                    continue;
                }

                string json;
                if (!TryReadSafeFile(file, out json) || !JsonUtil.ExtractBool(json, "is_completed", false))
                {
                    continue;
                }

                candidates.Add(file);
            }

            return candidates;
        }

        static List<FileInfo> SafeOperationFiles()
        {
            var result = new List<FileInfo>();
            try
            {
                var directory = new DirectoryInfo(OperationDirectory());
                if (!directory.Exists || IsReparsePoint(directory.Attributes))
                {
                    return result;
                }

                var files = directory.GetFiles("*.json", SearchOption.TopDirectoryOnly);
                Array.Sort(files, (left, right) => string.CompareOrdinal(left.Name, right.Name));
                for (var index = 0; index < files.Length; index++)
                {
                    var file = files[index];
                    var operationId = Path.GetFileNameWithoutExtension(file.Name);
                    if (IsSafeOperationId(operationId) && string.Equals(file.Extension, ".json", StringComparison.OrdinalIgnoreCase) && !IsReparsePoint(file.Attributes))
                    {
                        result.Add(file);
                    }
                }
            }
            catch
            {
                return result;
            }

            return result;
        }

        static bool TryReadSafeFile(FileInfo file, out string json)
        {
            json = string.Empty;
            try
            {
                if (IsReparsePoint(file.Attributes) || file.Length > 65536)
                {
                    return false;
                }

                json = File.ReadAllText(file.FullName);
                return !string.IsNullOrEmpty(json);
            }
            catch
            {
                return false;
            }
        }

        static bool IsReparsePoint(FileAttributes attributes)
        {
            return (attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
        }

        static string OperationStatus(PackageOperation operation)
        {
            if (!operation.Request.IsCompleted)
            {
                return "running";
            }

            if (operation.Request.Status == StatusCode.Success)
            {
                return "succeeded";
            }

            if (operation.Request.Status == StatusCode.Failure)
            {
                return "failed";
            }

            return operation.Request.Status.ToString().ToLowerInvariant();
        }

        static string OperationSummaryJson(string operationId, string kind, string status, string packageName, DateTime createdUtc, DateTime updatedUtc, string source, bool isCompleted)
        {
            var builder = new StringBuilder();
            builder.Append("{\"operation_id\":").Append(JsonUtil.String(operationId));
            builder.Append(",\"command\":").Append(JsonUtil.String(kind));
            builder.Append(",\"status\":").Append(JsonUtil.String(status));
            builder.Append(",\"package\":").Append(JsonUtil.String(packageName));
            AppendOperationDiagnosticsFields(builder, status, isCompleted, createdUtc, updatedUtc, source);
            builder.Append('}');
            return builder.ToString();
        }

        static string OperationListJson(List<string> summaries, int maxResults)
        {
            var count = Math.Min(summaries.Count, maxResults);
            var builder = new StringBuilder();
            builder.Append("{\"max_results\":").Append(maxResults);
            builder.Append(",\"total_available\":").Append(summaries.Count);
            builder.Append(",\"truncated\":").Append(JsonUtil.Bool(summaries.Count > count));
            builder.Append(",\"upm_log_diagnostics\":").Append(UpmLogDiagnosticsJson());
            builder.Append(",\"operations\":[");
            for (var index = 0; index < count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append(summaries[index]);
            }

            builder.Append("]}");
            return builder.ToString();
        }

        static string PersistedOperationJson(string operationId, string kind, string packageName, string packageSpec, string status, bool isCompleted, string resultJson, string errorMessage, DateTime createdUtc, DateTime updatedUtc, string source, bool inferredFromPackageList)
        {
            var builder = new StringBuilder();
            builder.Append("{\"operation_id\":").Append(JsonUtil.String(operationId));
            builder.Append(",\"kind\":").Append(JsonUtil.String(kind));
            builder.Append(",\"status\":").Append(JsonUtil.String(status));
            builder.Append(",\"is_completed\":").Append(JsonUtil.Bool(isCompleted));
            builder.Append(",\"package_name\":").Append(JsonUtil.String(packageName));
            builder.Append(",\"package_spec\":").Append(JsonUtil.String(packageSpec));
            builder.Append(",\"inferred_from_current_package_list\":").Append(JsonUtil.Bool(inferredFromPackageList));
            AppendOperationDiagnosticsFields(builder, status, isCompleted, createdUtc, updatedUtc, source);
            if (!string.IsNullOrEmpty(resultJson))
            {
                builder.Append(",\"result\":").Append(resultJson);
            }

            if (!string.IsNullOrEmpty(errorMessage))
            {
                builder.Append(",\"error\":{\"message\":").Append(JsonUtil.String(errorMessage)).Append('}');
            }

            builder.Append('}');
            return builder.ToString();
        }

        static void AppendOperationDiagnosticsFields(StringBuilder builder, string status, bool isCompleted, DateTime createdUtc, DateTime updatedUtc, string source)
        {
            var nowUtc = DateTime.UtcNow;
            var ageMs = NonNegativeMilliseconds(createdUtc, nowUtc);
            var lastUpdateAgeMs = NonNegativeMilliseconds(updatedUtc, nowUtc);
            var classification = OperationLifecycle(status, isCompleted, source, ageMs, lastUpdateAgeMs);

            builder.Append(",\"created_utc\":").Append(JsonUtil.String(createdUtc.ToString("o")));
            builder.Append(",\"updated_utc\":").Append(JsonUtil.String(updatedUtc.ToString("o")));
            builder.Append(",\"age_ms\":").Append(ageMs);
            builder.Append(",\"last_update_age_ms\":").Append(lastUpdateAgeMs);
            builder.Append(",\"source\":").Append(JsonUtil.String(source));
            builder.Append(",\"lifecycle\":").Append(JsonUtil.String(classification));
            builder.Append(",\"progress_supported\":false");
            builder.Append(",\"unity_package_manager_cancel_supported\":false");
            builder.Append(",\"operation_diagnostics\":").Append(OperationDiagnosticsJson(classification, source));
            builder.Append(",\"upm_log_diagnostics\":").Append(UpmLogDiagnosticsJson());
            builder.Append(",\"next_valid_actions\":").Append(PackageOperationNextActionsJson(classification, isCompleted));
        }

        static string OperationDiagnosticsJson(string classification, string source)
        {
            return "{\"classification\":" + JsonUtil.String(classification)
                + ",\"possibly_stuck\":" + JsonUtil.Bool(classification == "long_running" || classification == "stale_persisted_running")
                + ",\"last_observed_source\":" + JsonUtil.String(source)
                + ",\"progress_supported\":false"
                + ",\"unity_package_manager_cancel_supported\":false"
                + ",\"thresholds\":{\"long_running_ms\":" + PackageOperationLongRunningMs + ",\"stale_persisted_ms\":" + PackageOperationStalePersistedMs + "}"
                + ",\"hint\":" + JsonUtil.String(OperationHint(classification))
                + "}";
        }

        static string OperationHint(string classification)
        {
            if (classification == "stale_persisted_running")
            {
                return "This operation is only present as an old persisted running record after reload; check package.list/compile.status and use package.operation.prune only for completed records.";
            }

            if (classification == "persisted_running_after_reload")
            {
                return "The in-memory Unity PackageManager request was lost across reload; poll briefly, then check package.list and compile.status.";
            }

            if (classification == "long_running")
            {
                return "Unity PackageManager has not completed yet and does not expose progress or stable cancellation; wait, inspect the Package Manager UI, or report a problem if it remains stuck.";
            }

            if (classification == "completed")
            {
                return "Operation is complete; verify package.list and compile.status before relying on the package change.";
            }

            if (classification == "failed")
            {
                return "Operation failed; inspect error and upm_log_diagnostics, verify package.list/compile.status, then retry only after package cache locks are cleared.";
            }

            return "Operation is still running; poll package.operation.status and avoid starting conflicting package mutations.";
        }

        static string PackageOperationNextActionsJson(string classification, bool isCompleted)
        {
            if (isCompleted || classification == "completed")
            {
                return "[\"package.list\",\"compile.status\"]";
            }

            if (classification == "failed")
            {
                return "[\"package.list\",\"compile.status\",\"inspect_upm_log_diagnostics\",\"close_unity_editors_for_project_before_retry\",\"diagnostics.report_problem_if_failure_repeats\"]";
            }

            if (classification == "long_running" || classification == "stale_persisted_running")
            {
                return "[\"poll_package_operation_status\",\"package.list\",\"compile.status\",\"inspect_unity_package_manager_ui\",\"restart_unity_only_if_editor_is_stuck\",\"diagnostics.report_problem_if_stuck_persists\"]";
            }

            return "[\"poll_package_operation_status\",\"package.list\",\"wait_for_unity_package_manager\"]";
        }

        static string OperationLifecycle(string status, bool isCompleted, string source, long ageMs, long lastUpdateAgeMs)
        {
            if (isCompleted)
            {
                return string.Equals(status, "failed", StringComparison.Ordinal) ? "failed" : "completed";
            }

            if (!string.Equals(status, "running", StringComparison.Ordinal))
            {
                return "not_completed";
            }

            if (string.Equals(source, "persisted", StringComparison.Ordinal))
            {
                return lastUpdateAgeMs >= PackageOperationStalePersistedMs ? "stale_persisted_running" : "persisted_running_after_reload";
            }

            return ageMs >= PackageOperationLongRunningMs ? "long_running" : "running";
        }

        static string UpmLogDiagnosticsJson()
        {
            var evidence = FindUpmPackageCacheRenameFailure();
            if (evidence == null)
            {
                return "{\"status\":\"not_detected\",\"classification\":\"none\",\"max_tail_bytes\":" + UpmLogTailBytes + ",\"checked_log_paths\":" + JsonArrayStrings(UpmLogCandidatePaths()) + "}";
            }

            return "{\"status\":\"blocked\""
                + ",\"classification\":\"upm_packagecache_rename_eperm\""
                + ",\"error_code\":\"EPERM\""
                + ",\"operation\":\"rename\""
                + ",\"package_name\":" + JsonUtil.String(evidence.PackageName)
                + ",\"source_path\":" + JsonUtil.String(evidence.SourcePath)
                + ",\"target_path\":" + JsonUtil.String(evidence.TargetPath)
                + ",\"source_project_relative\":" + JsonStringOrNull(ProjectRelativeOrNull(evidence.SourcePath))
                + ",\"target_project_relative\":" + JsonStringOrNull(ProjectRelativeOrNull(evidence.TargetPath))
                + ",\"log_path\":" + JsonUtil.String(evidence.LogPath)
                + ",\"log_last_write_utc\":" + JsonUtil.String(evidence.LogLastWriteUtc.ToString("o"))
                + ",\"message\":" + JsonUtil.String(Bounded(evidence.Message, UpmEvidenceMaxChars))
                + ",\"destructive_cleanup_allowed\":false"
                + ",\"next_valid_actions\":[\"close_unity_editors_for_project_before_retry\",\"inspect_process_locks_for_packagecache_paths\",\"retry_package_resolve_after_locks_clear\",\"do_not_delete_packagecache_without_explicit_confirmation\"]"
                + "}";
        }

        static UpmRenameFailureEvidence FindUpmPackageCacheRenameFailure()
        {
            var candidates = UpmLogCandidatePaths();
            for (var index = 0; index < candidates.Count; index++)
            {
                var path = candidates[index];
                try
                {
                    var file = new FileInfo(path);
                    if (!file.Exists || IsReparsePoint(file.Attributes))
                    {
                        continue;
                    }

                    var tail = ReadFileTail(file, UpmLogTailBytes);
                    if (TryExtractUpmRenameFailure(tail, path, file.LastWriteTimeUtc, out var evidence))
                    {
                        return evidence;
                    }
                }
                catch
                {
                    // Log diagnostics are best-effort and must not block package operation status.
                }
            }

            return null;
        }

        static bool TryExtractUpmRenameFailure(string text, string logPath, DateTime logLastWriteUtc, out UpmRenameFailureEvidence evidence)
        {
            evidence = null;
            if (string.IsNullOrEmpty(text) || text.IndexOf("EPERM", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            var match = LastSuccessfulMatch(UpmRenameEpermRegex, text);
            if (match.Success)
            {
                evidence = new UpmRenameFailureEvidence(logPath, logLastWriteUtc, match.Groups["package"].Value, match.Groups["source"].Value, match.Groups["target"].Value, match.Value);
                return true;
            }

            match = LastSuccessfulMatch(UpmFailedRenameEpermRegex, text);
            if (match.Success)
            {
                var target = match.Groups["target"].Value;
                evidence = new UpmRenameFailureEvidence(logPath, logLastWriteUtc, PackageNameFromPackageCachePath(target), match.Groups["source"].Value, target, match.Value);
                return true;
            }

            return false;
        }

        static Match LastSuccessfulMatch(Regex regex, string text)
        {
            var matches = regex.Matches(text);
            return matches.Count == 0 ? Match.Empty : matches[matches.Count - 1];
        }

        static List<string> UpmLogCandidatePaths()
        {
            var paths = new List<string>();
            AddUniquePath(paths, Path.Combine(ProjectPaths.ProjectRoot, "Logs", "sgg-unity-mcp-editor-check.log"));
            AddUniquePath(paths, Application.consoleLogPath);
            AddUniquePath(paths, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unity", "Editor", "Editor.log"));
            return paths;
        }

        static void AddUniquePath(List<string> paths, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            for (var index = 0; index < paths.Count; index++)
            {
                if (string.Equals(paths[index], path, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            paths.Add(path);
        }

        static string ReadFileTail(FileInfo file, int maxBytes)
        {
            var bytesToRead = (int)Math.Min(Math.Max(0, file.Length), maxBytes);
            if (bytesToRead <= 0)
            {
                return string.Empty;
            }

            using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.Seek(-bytesToRead, SeekOrigin.End);
                var buffer = new byte[bytesToRead];
                var read = stream.Read(buffer, 0, buffer.Length);
                return Encoding.UTF8.GetString(buffer, 0, read);
            }
        }

        static bool UpmFailureMatchesOperation(UpmRenameFailureEvidence evidence, string packageName, string packageSpec)
        {
            if (evidence == null)
            {
                return false;
            }

            if (string.IsNullOrEmpty(packageName) && string.IsNullOrEmpty(packageSpec))
            {
                return true;
            }

            return (!string.IsNullOrEmpty(packageName) && (string.Equals(evidence.PackageName, packageName, StringComparison.OrdinalIgnoreCase) || evidence.TargetPath.IndexOf(packageName, StringComparison.OrdinalIgnoreCase) >= 0))
                || (!string.IsNullOrEmpty(packageSpec) && (packageSpec.IndexOf(evidence.PackageName, StringComparison.OrdinalIgnoreCase) >= 0 || evidence.TargetPath.IndexOf(packageSpec, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        static string UpmFailureMessage(UpmRenameFailureEvidence evidence)
        {
            return "Unity Package Manager PackageCache rename failed with EPERM: " + evidence.Message;
        }

        static string PackageNameFromPackageCachePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            var normalized = path.Replace('\\', '/');
            var slash = normalized.LastIndexOf('/');
            var leaf = slash >= 0 ? normalized.Substring(slash + 1) : normalized;
            var at = leaf.IndexOf('@');
            return at > 0 ? leaf.Substring(0, at) : leaf;
        }

        static string ProjectRelativeOrNull(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            try
            {
                var root = Path.GetFullPath(ProjectPaths.ProjectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var fullPath = Path.GetFullPath(path);
                if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return fullPath.Substring(root.Length).Replace('\\', '/');
            }
            catch
            {
                return null;
            }
        }

        static string JsonStringOrNull(string value)
        {
            return value == null ? "null" : JsonUtil.String(value);
        }

        static string Bounded(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, maxChars);
        }

        static string JsonArrayStrings(List<string> values)
        {
            var builder = new StringBuilder();
            builder.Append('[');
            for (var index = 0; index < values.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonUtil.String(values[index]));
            }

            builder.Append(']');
            return builder.ToString();
        }

        static long NonNegativeMilliseconds(DateTime fromUtc, DateTime toUtc)
        {
            var milliseconds = (long)Math.Max(0, (toUtc - fromUtc).TotalMilliseconds);
            return milliseconds;
        }

        static bool IsInstallCompletionKind(string kind)
        {
            return string.Equals(kind, "install", StringComparison.Ordinal)
                || string.Equals(kind, "install_git", StringComparison.Ordinal)
                || string.Equals(kind, "update", StringComparison.Ordinal);
        }

        static bool InstalledPackageSatisfiesOperation(string kind, string packageSpec, PackageManagerPackageInfo installedPackage)
        {
            if (installedPackage == null)
            {
                return false;
            }

            if (string.IsNullOrEmpty(packageSpec))
            {
                return true;
            }

            if (string.Equals(kind, "install", StringComparison.Ordinal) || string.Equals(kind, "install_git", StringComparison.Ordinal))
            {
                return PackageSpecMatchesInstalledPackageId(packageSpec, installedPackage.packageId);
            }

            if (!string.Equals(kind, "update", StringComparison.Ordinal))
            {
                return false;
            }

            var atIndex = packageSpec.LastIndexOf('@');
            if (atIndex < 0 || atIndex + 1 >= packageSpec.Length)
            {
                return true;
            }

            return string.Equals(installedPackage.version, packageSpec.Substring(atIndex + 1), StringComparison.Ordinal);
        }

        static bool PackageSpecMatchesInstalledPackageId(string packageSpec, string packageId)
        {
            if (string.IsNullOrEmpty(packageSpec) || string.IsNullOrEmpty(packageId))
            {
                return false;
            }

            if (packageId.IndexOf(packageSpec, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (packageSpec.StartsWith("git+https://", StringComparison.Ordinal) && packageId.IndexOf(packageSpec.Substring(4), StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }

        static void WritePersistedOperation(string operationId, string json)
        {
            try
            {
                Directory.CreateDirectory(OperationDirectory());
                File.WriteAllText(OperationFilePath(operationId), json);
            }
            catch
            {
                // Operation persistence is best-effort; the in-memory request remains authoritative while the domain is loaded.
            }
        }

        static bool TryReadPersistedOperation(string operationId, out string json)
        {
            json = string.Empty;
            try
            {
                var path = OperationFilePath(operationId);
                var file = new FileInfo(path);
                if (!file.Exists)
                {
                    return false;
                }

                return TryReadSafeFile(file, out json);
            }
            catch
            {
                return false;
            }
        }

        static string OperationDirectory()
        {
            return Path.Combine(ProjectPaths.ProjectRoot, "Library", "SGGUnityMcp", "package-operations");
        }

        static string OperationFilePath(string operationId)
        {
            return Path.Combine(OperationDirectory(), operationId + ".json");
        }

        static string PackageInfoJson(PackageManagerPackageInfo package)
        {
            if (package == null)
            {
                return "{}";
            }

            return "{\"name\":" + JsonUtil.String(package.name)
                + ",\"version\":" + JsonUtil.String(package.version)
                + ",\"display_name\":" + JsonUtil.String(package.displayName)
                + ",\"source\":" + JsonUtil.String(package.source.ToString())
                + "}";
        }

        static string PackageInfoListJson(string packageName, int maxResults, bool includeSamples)
        {
            var packages = PackageManagerPackageInfo.GetAllRegisteredPackages();
            Array.Sort(packages, (left, right) => string.Compare(left.name, right.name, StringComparison.Ordinal));

            var matches = new List<PackageManagerPackageInfo>();
            for (var index = 0; index < packages.Length; index++)
            {
                if (MatchesPackageInfoQuery(packages[index], packageName))
                {
                    matches.Add(packages[index]);
                }
            }

            var count = Math.Min(matches.Count, maxResults);
            var returnedPackages = matches.GetRange(0, count).ToArray();
            var metadataBatch = LocalPackageMetadataInspector.InspectPackages(returnedPackages, matches.Count > count);
            var builder = new StringBuilder();
            builder.Append("{\"installed_only\":true");
            builder.Append(",\"compatible_with\":\"Unity.PackageManager.GetData\"");
            builder.Append(",\"package_name\":").Append(JsonUtil.String(packageName));
            builder.Append(",\"include_samples\":").Append(JsonUtil.Bool(includeSamples));
            builder.Append(",\"max_results\":").Append(maxResults);
            builder.Append(",\"total_available\":").Append(matches.Count);
            builder.Append(",\"truncated\":").Append(JsonUtil.Bool(matches.Count > count));
            LocalPackageMetadataInspector.AppendSummaryJsonFields(builder, metadataBatch.Summary);
            builder.Append(",\"packages\":[");
            for (var index = 0; index < count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append(InstalledPackageInfoJson(matches[index], metadataBatch.Evidence[index], includeSamples));
            }

            builder.Append("]}");
            return builder.ToString();
        }

        static bool MatchesPackageInfoQuery(PackageManagerPackageInfo package, string packageName)
        {
            if (string.IsNullOrEmpty(packageName))
            {
                return true;
            }

            return ContainsOrdinalIgnoreCase(package.name, packageName) || ContainsOrdinalIgnoreCase(package.packageId, packageName);
        }

        static bool ContainsOrdinalIgnoreCase(string value, string search)
        {
            return !string.IsNullOrEmpty(value) && value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string InstalledPackageInfoJson(PackageManagerPackageInfo package, LocalPackageMetadataEvidence metadataEvidence, bool includeSamples)
        {
            var samplesJson = PackageSamplesJson(package, includeSamples, out var samplesTruncated);
            var builder = new StringBuilder();
            builder.Append("{\"name\":").Append(JsonUtil.String(package.name));
            builder.Append(",\"version\":").Append(JsonUtil.String(package.version));
            builder.Append(",\"display_name\":").Append(JsonUtil.String(package.displayName));
            builder.Append(",\"description\":").Append(JsonUtil.String(Truncate(package.description, MaxDescriptionChars)));
            builder.Append(",\"source\":").Append(JsonUtil.String(package.source.ToString()));
            builder.Append(",\"embedded\":").Append(JsonUtil.Bool(package.source == PackageSource.Embedded));
            LocalPackageMetadataInspector.AppendPackageJsonFields(builder, metadataEvidence);
            builder.Append(",\"samples\":").Append(samplesJson);
            builder.Append(",\"samples_truncated\":").Append(JsonUtil.Bool(samplesTruncated));
            builder.Append('}');
            return builder.ToString();
        }

        static string PackageSamplesJson(PackageManagerPackageInfo package, bool includeSamples, out bool truncated)
        {
            truncated = false;
            if (!includeSamples)
            {
                return "[]";
            }

            var samples = FindPackageSamples(package.name, package.version);

            var count = Math.Min(samples.Count, MaxSamplesPerPackage);
            truncated = samples.Count > count;
            var builder = new StringBuilder();
            builder.Append('[');
            for (var index = 0; index < count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"name\":").Append(JsonUtil.String(Truncate(samples[index].DisplayName, MaxDescriptionChars)));
                builder.Append(",\"description\":").Append(JsonUtil.String(Truncate(samples[index].Description, MaxDescriptionChars)));
                builder.Append(",\"imported\":").Append(JsonUtil.Bool(samples[index].Imported));
                builder.Append('}');
            }

            builder.Append(']');
            return builder.ToString();
        }

        static string Truncate(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Length <= maxChars ? value : value.Substring(0, maxChars);
        }

        static List<PackageSampleInfo> FindPackageSamples(string packageName, string packageVersion)
        {
            var samples = new List<PackageSampleInfo>();
            var sampleType = FindPackageManagerUiSampleType();
            if (sampleType == null)
            {
                return samples;
            }

            var findByPackage = sampleType.GetMethod(
                "FindByPackage",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), typeof(string) },
                null);
            if (findByPackage == null)
            {
                return samples;
            }

            try
            {
                var result = findByPackage.Invoke(null, new object[] { packageName, packageVersion }) as System.Collections.IEnumerable;
                if (result == null)
                {
                    return samples;
                }

                foreach (var sample in result)
                {
                    if (sample == null)
                    {
                        continue;
                    }

                    samples.Add(new PackageSampleInfo
                    {
                        DisplayName = GetSampleString(sample, "displayName"),
                        Description = GetSampleString(sample, "description"),
                        Imported = GetSampleBool(sample, "isImported"),
                    });
                }
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning("SGG Unity MCP could not inspect Package Manager samples: " + exception.Message);
            }

            return samples;
        }

        static Type FindPackageManagerUiSampleType()
        {
            const string sampleTypeName = "UnityEditor.PackageManager.UI.Sample";
            foreach (var assembly in EditorAssemblyDiscovery.LoadedAssemblies())
            {
                var loadedType = assembly.GetType(sampleTypeName, false);
                if (loadedType != null)
                {
                    return loadedType;
                }
            }

            return Type.GetType(sampleTypeName + ", UnityEditor.PackageManagerUI", false)
                ?? Type.GetType(sampleTypeName + ", UnityEditor.PackageManagerUIModule", false);
        }

        static string GetSampleString(object sample, string propertyName)
        {
            var property = sample.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            return property == null ? string.Empty : property.GetValue(sample, null) as string ?? string.Empty;
        }

        static bool GetSampleBool(object sample, string propertyName)
        {
            var property = sample.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            var value = property == null ? null : property.GetValue(sample, null);
            return value is bool && (bool)value;
        }

        static string SearchResultsJson(PackageManagerPackageInfo[] packages, int maxResults)
        {
            var total = packages == null ? 0 : packages.Length;
            var count = Math.Min(total, maxResults);
            var builder = new StringBuilder();
            builder.Append("{\"max_results\":").Append(maxResults);
            builder.Append(",\"total_available\":").Append(total);
            builder.Append(",\"truncated\":").Append(JsonUtil.Bool(total > count));
            builder.Append(",\"results\":[");
            for (var index = 0; index < count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append(PackageInfoJson(packages[index]));
            }

            builder.Append("]}");
            return builder.ToString();
        }

        sealed class PackageOperation
        {
            readonly Func<string> m_resultJson;

            public PackageOperation(string id, string kind, string packageName, string packageSpec, Request request, Func<string> resultJson)
            {
                Id = id;
                Kind = kind;
                PackageName = packageName;
                PackageSpec = packageSpec;
                Request = request;
                m_resultJson = resultJson;
                StartedUtc = DateTime.UtcNow;
            }

            public string Id { get; }
            public string Kind { get; }
            public string PackageName { get; }
            public string PackageSpec { get; }
            public Request Request { get; }
            public DateTime StartedUtc { get; }

            public string ToJson()
            {
                var builder = new StringBuilder();
                var isCompleted = Request.IsCompleted;
                var status = RequestStatus();

                builder.Append("{\"operation_id\":").Append(JsonUtil.String(Id));
                builder.Append(",\"kind\":").Append(JsonUtil.String(Kind));
                builder.Append(",\"status\":").Append(JsonUtil.String(status));
                builder.Append(",\"is_completed\":").Append(JsonUtil.Bool(isCompleted));
                builder.Append(",\"package_name\":").Append(JsonUtil.String(PackageName));
                builder.Append(",\"package_spec\":").Append(JsonUtil.String(PackageSpec));
                AppendOperationDiagnosticsFields(builder, status, isCompleted, StartedUtc, DateTime.UtcNow, "memory");

                if (isCompleted && Request.Status == StatusCode.Success)
                {
                    builder.Append(",\"result\":").Append(m_resultJson == null ? "{}" : m_resultJson());
                }
                else if (isCompleted && Request.Status == StatusCode.Failure)
                {
                    var message = Request.Error == null ? "PackageManager request failed" : Request.Error.message;
                    builder.Append(",\"error\":{\"message\":").Append(JsonUtil.String(message)).Append('}');
                }

                builder.Append('}');
                return builder.ToString();
            }

            string RequestStatus()
            {
                if (!Request.IsCompleted)
                {
                    return "running";
                }

                if (Request.Status == StatusCode.Success)
                {
                    return "succeeded";
                }

                if (Request.Status == StatusCode.Failure)
                {
                    return "failed";
                }

                return Request.Status.ToString().ToLowerInvariant();
            }
        }

        sealed class UpmRenameFailureEvidence
        {
            public UpmRenameFailureEvidence(string logPath, DateTime logLastWriteUtc, string packageName, string sourcePath, string targetPath, string message)
            {
                LogPath = logPath ?? string.Empty;
                LogLastWriteUtc = logLastWriteUtc;
                PackageName = packageName ?? string.Empty;
                SourcePath = sourcePath ?? string.Empty;
                TargetPath = targetPath ?? string.Empty;
                Message = message ?? string.Empty;
            }

            public string LogPath { get; }
            public DateTime LogLastWriteUtc { get; }
            public string PackageName { get; }
            public string SourcePath { get; }
            public string TargetPath { get; }
            public string Message { get; }
        }

        sealed class PackageSampleInfo
        {
            public string DisplayName;
            public string Description;
            public bool Imported;
        }
    }
}
