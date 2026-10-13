using System;
using System.Collections.Generic;
using System.IO;
using SGG.UnityMCP.Editor.Bridge;
using UnityEditor.PackageManager;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class CommandManifestScanner
    {
        const string BuiltInPackageId = "com.sungeargames.unity-mcp";
        const string ProjectManifestCandidateReason = "project_asset_manifests_are_not_registered";

        public static void ScanInto(CommandRegistry registry)
        {
            if (registry == null)
            {
                throw new ArgumentNullException(nameof(registry));
            }

            var total = Stopwatch.StartNew();
            BridgeReloadTrace.Log("command_manifest.package_scan.start");
            PackageInfo[] packages;
            try
            {
                var step = Stopwatch.StartNew();
                BridgeReloadTrace.Log("command_manifest.get_all_registered_packages.start");
                packages = PackageInfo.GetAllRegisteredPackages();
                BridgeReloadTrace.Log("command_manifest.get_all_registered_packages.end", packages == null ? "count=0" : "count=" + packages.Length, step.ElapsedMilliseconds);
            }
            catch (Exception exception)
            {
                BridgeReloadTrace.LogError("command_manifest.get_all_registered_packages.error", exception);
                registry.AddManifestDiagnostic("package_scan_failed", string.Empty, string.Empty, string.Empty, exception.Message);
                Debug.LogWarning("SGG Unity MCP could not enumerate packages for command manifests: " + exception.Message);
                ScanProjectAssetManifestCandidates(registry);
                BridgeReloadTrace.Log("command_manifest.package_scan.end", total.ElapsedMilliseconds);
                return;
            }

            foreach (var package in packages)
            {
                try
                {
                    if (package == null || string.IsNullOrEmpty(package.name) || string.IsNullOrEmpty(package.resolvedPath))
                    {
                        continue;
                    }

                    TryReadManifest(
                        registry,
                        package.name,
                        package.version,
                        Path.Combine(package.resolvedPath, "Editor", "Mcp", "mcp.commands.json"),
                        "Packages/" + package.name + "/Editor/Mcp/mcp.commands.json");
                    TryReadManifest(
                        registry,
                        package.name,
                        package.version,
                        Path.Combine(package.resolvedPath, "mcp.commands.json"),
                        "Packages/" + package.name + "/mcp.commands.json");
                }
                catch (Exception exception)
                {
                    var packageName = package == null ? string.Empty : package.name;
                    var packageVersion = package == null ? string.Empty : package.version;
                    registry.AddManifestDiagnostic("package_manifest_scan_failed", packageName, packageVersion, string.Empty, exception.Message);
                    Debug.LogWarning("SGG Unity MCP could not scan command manifests for package " + packageName + ": " + exception.Message);
                }
            }

            ScanProjectAssetManifestCandidates(registry);
            BridgeReloadTrace.Log("command_manifest.package_scan.end", total.ElapsedMilliseconds);
        }

        static void TryReadManifest(CommandRegistry registry, string packageName, string packageVersion, string manifestPath, string unityManifestPath)
        {
            try
            {
                if (!File.Exists(manifestPath))
                {
                    return;
                }

                var manifestJson = File.ReadAllText(manifestPath);
                var manifest = JsonUtility.FromJson<CommandManifest>(manifestJson);
                if (manifest?.commands == null)
                {
                    return;
                }

                var rawCommandsById = RawCommandObjectsById(manifestJson);
                foreach (var command in manifest.commands)
                {
                    if (command == null || string.IsNullOrEmpty(command.id))
                    {
                        continue;
                    }

                    rawCommandsById.TryGetValue(command.id, out var rawCommandJson);
                    var packageId = packageName;
                    var displayPackage = string.IsNullOrEmpty(manifest.package) ? packageName : manifest.package;
                    var builtIn = packageName == BuiltInPackageId && packageId == BuiltInPackageId;
                    registry.RegisterManifestCommand(new CommandDescriptor
                    {
                        Id = command.id,
                        Title = string.IsNullOrEmpty(command.title) ? command.id : command.title,
                        Description = command.description ?? string.Empty,
                        Package = displayPackage,
                        Risk = string.IsNullOrEmpty(command.risk) ? "read" : command.risk,
                        Idempotency = string.IsNullOrEmpty(command.idempotency) ? "safe" : command.idempotency,
                        TimeoutMs = command.timeout_ms <= 0 ? 10000 : command.timeout_ms,
                        SchemaJson = FirstNonEmpty(ExtractOptionalObject(rawCommandJson, "schema"), command.schema_json, "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"),
                        Tags = command.tags ?? new string[0],
                        Capability = command.capability ?? string.Empty,
                        RequiresEditorStatus = command.requires_editor_status ?? new string[0],
                        ResultShape = command.result_shape ?? string.Empty,
                        OutputLimitJson = FirstNonEmpty(ExtractOptionalObject(rawCommandJson, "output_limit"), command.output_limit_json, string.Empty),
                        SupportsJob = command.supports_job,
                        SupportsCancel = command.supports_cancel,
                        WrapperWaitJson = FirstNonEmpty(ExtractOptionalObject(rawCommandJson, "wrapper_wait"), command.wrapper_wait_json, string.Empty),
                        FeatureFlag = command.feature_flag ?? string.Empty,
                        Source = "manifest",
                        PackageId = packageId,
                        PackageVersion = packageVersion ?? string.Empty,
                        ManifestPath = unityManifestPath,
                        Trust = builtIn ? "builtin" : "untrusted_package",
                    }, command.handler);
                }
            }
            catch (Exception exception)
            {
                registry.AddManifestDiagnostic("manifest_read_failed", packageName, packageVersion, unityManifestPath, exception.Message);
                Debug.LogWarning("SGG Unity MCP could not read command manifest " + unityManifestPath + ": " + exception.Message);
            }
        }

        static void ScanProjectAssetManifestCandidates(CommandRegistry registry)
        {
            var scriptsRoot = Path.Combine(Application.dataPath, "Scripts");
            try
            {
                if (!Directory.Exists(scriptsRoot))
                {
                    return;
                }

                var files = Directory.GetFiles(scriptsRoot, "mcp.commands.json", SearchOption.AllDirectories);
                Array.Sort(files, StringComparer.Ordinal);
                for (var index = 0; index < files.Length; index++)
                {
                    registry.AddProjectManifestCandidate(ToAssetsRelativePath(files[index]), ProjectManifestCandidateReason);
                }
            }
            catch (Exception exception)
            {
                registry.AddManifestDiagnostic("project_manifest_candidate_scan_failed", string.Empty, string.Empty, "Assets/Scripts", exception.Message);
                Debug.LogWarning("SGG Unity MCP could not scan Assets/Scripts command manifest candidates: " + exception.Message);
            }
        }

        static string ToAssetsRelativePath(string fullPath)
        {
            var normalizedFullPath = (fullPath ?? string.Empty).Replace('\\', '/');
            var normalizedAssetsPath = (Application.dataPath ?? string.Empty).Replace('\\', '/');
            if (normalizedFullPath.StartsWith(normalizedAssetsPath + "/", StringComparison.Ordinal))
            {
                return "Assets/" + normalizedFullPath.Substring(normalizedAssetsPath.Length + 1);
            }

            return normalizedFullPath;
        }

        [Serializable]
        sealed class CommandManifest
        {
            public int schema_version;
            public string package;
            public CommandManifestEntry[] commands;
        }

        [Serializable]
        sealed class CommandManifestEntry
        {
            public string id;
            public string title;
            public string description;
            public string handler;
            public string risk;
            public string idempotency;
            public int timeout_ms;
            public string schema_json;
            public string[] tags;
            public string capability;
            public string[] requires_editor_status;
            public string result_shape;
            public string output_limit_json;
            public bool supports_job;
            public bool supports_cancel;
            public string wrapper_wait_json;
            public string feature_flag;
        }

        static Dictionary<string, string> RawCommandObjectsById(string manifestJson)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var arrayStart = FindArrayStart(manifestJson, "commands");
            if (arrayStart < 0)
            {
                return result;
            }

            var depth = 0;
            var objectStart = -1;
            var inString = false;
            var escaped = false;
            for (var index = arrayStart + 1; index < manifestJson.Length; index++)
            {
                var character = manifestJson[index];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (character == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (character == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (inString)
                {
                    continue;
                }

                if (character == '{')
                {
                    if (depth == 0)
                    {
                        objectStart = index;
                    }
                    depth++;
                }
                else if (character == '}')
                {
                    depth--;
                    if (depth == 0 && objectStart >= 0)
                    {
                        var rawCommand = manifestJson.Substring(objectStart, index - objectStart + 1);
                        var commandId = JsonUtil.ExtractString(rawCommand, "id");
                        if (!string.IsNullOrEmpty(commandId))
                        {
                            result[commandId] = rawCommand;
                        }
                        objectStart = -1;
                    }
                }
                else if (character == ']' && depth == 0)
                {
                    break;
                }
            }

            return result;
        }

        static int FindArrayStart(string json, string property)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(property))
            {
                return -1;
            }

            var propertyIndex = json.IndexOf("\"" + property + "\"", StringComparison.Ordinal);
            if (propertyIndex < 0)
            {
                return -1;
            }

            var colon = json.IndexOf(':', propertyIndex + property.Length + 2);
            if (colon < 0)
            {
                return -1;
            }

            for (var index = colon + 1; index < json.Length; index++)
            {
                if (char.IsWhiteSpace(json[index]))
                {
                    continue;
                }

                return json[index] == '[' ? index : -1;
            }

            return -1;
        }

        static string ExtractOptionalObject(string json, string property)
        {
            if (string.IsNullOrEmpty(json) || json.IndexOf("\"" + property + "\"", StringComparison.Ordinal) < 0)
            {
                return string.Empty;
            }

            return JsonUtil.ExtractObject(json, property);
        }

        static string FirstNonEmpty(params string[] values)
        {
            for (var index = 0; index < values.Length; index++)
            {
                if (!string.IsNullOrEmpty(values[index]))
                {
                    return values[index];
                }
            }

            return string.Empty;
        }
    }
}
