using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using SGG.UnityMCP.Editor.Paths;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Setup
{
    internal static class OpenCodePresentationSetupManager
    {
        internal enum SetupAction
        {
            Status,
            DryRun,
            Write,
            Remove,
        }

        internal sealed class PresentationStatus
        {
            public bool Parsed;
            public bool CommandSucceeded;
            public bool OutcomeUncertain;
            public string Error = string.Empty;
            public string RawJson = string.Empty;
            public string Workspace = string.Empty;
            public string ArtifactState = "not_checked";
            public string BundledVersion = string.Empty;
            public string InstalledVersion = string.Empty;
            public string BundledHash = string.Empty;
            public string ConfigFile = "opencode.jsonc";
            public string ConfigState = "not_checked";
            public int OwnedEntries;
            public string Recovery = "none";
            public bool Applied;
            public bool RestartRequired;
            public string[] Changes = Array.Empty<string>();
            public string[] BlockedReasons = Array.Empty<string>();

            public bool Blocked => BlockedReasons != null && BlockedReasons.Length > 0;

            public string Summary()
            {
                if (!Parsed)
                {
                    return string.IsNullOrEmpty(Error) ? "Presentation status is unavailable." : Error;
                }

                var summary = "artifact=" + ArtifactState + ", config=" + ConfigState;
                if (Blocked)
                {
                    summary += ", blocked=" + string.Join(",", BlockedReasons);
                }
                if (RestartRequired)
                {
                    summary += ", restart required";
                }
                return summary;
            }

            public string RedactedDiagnostics()
            {
                var diagnostics = new RedactedDiagnosticsPayload
                {
                    schema_version = 1,
                    setup = "opencode-presentation",
                    artifact_state = ArtifactState,
                    bundled_version = BundledVersion,
                    installed_version = InstalledVersion,
                    bundled_hash = BundledHash,
                    config_file = ConfigFile,
                    config_state = ConfigState,
                    owned_entries = OwnedEntries,
                    recovery = Recovery,
                    applied = Applied,
                    restart_required = RestartRequired,
                    changes = Changes ?? Array.Empty<string>(),
                    blocked_reasons = BlockedReasons ?? Array.Empty<string>(),
                };
                return JsonUtility.ToJson(diagnostics, true);
            }
        }

        internal sealed class ClientProbeStatus
        {
            public string OpenCode = "Not checked.";
            public string OpenChamber = "Not checked.";
        }

        public static PresentationStatus Run(string workspace, SetupAction action, string expectedWorkspaceHash = null)
        {
            if (string.IsNullOrWhiteSpace(workspace))
            {
                return Failure("Select a valid OpenCode workspace first.");
            }

            string fullWorkspace;
            try
            {
                fullWorkspace = Path.GetFullPath(workspace);
            }
            catch
            {
                return Failure("Selected OpenCode workspace path is invalid.");
            }
            if (!Directory.Exists(fullWorkspace))
            {
                return Failure("Selected OpenCode workspace directory does not exist.");
            }

            var gatewayStatus = GatewayInstaller.GetStatus();
            if (!gatewayStatus.PackageMetadataValid || string.IsNullOrEmpty(gatewayStatus.PackageRuntimeCliPath) || !File.Exists(gatewayStatus.PackageRuntimeCliPath))
            {
                return Failure("Bundled Gateway runtime is unavailable or failed integrity validation.");
            }
            var nodeStatus = NodeRuntimeInstaller.GetStatus();
            if (!nodeStatus.IsAvailable || string.IsNullOrEmpty(nodeStatus.NodePath))
            {
                return Failure("Node 22+ is unavailable. Install Portable Node first.");
            }

            if (!BoundedProcessOperation.TryStart(
                    nodeStatus.NodePath,
                    BuildArgumentList(gatewayStatus.PackageRuntimeCliPath, fullWorkspace, action),
                    gatewayStatus.PackageRuntimeRoot,
                    TimeSpan.FromSeconds(30),
                    out var operation,
                    out var startError))
            {
                return Failure(startError);
            }
            using (operation)
            {
                while (!operation.Poll()) Thread.Sleep(25);
                var status = ParseResult(operation.StandardOutput, operation.ExitCode == 0, operation.StandardError, action, expectedWorkspaceHash);
                if (!operation.TimedOut)
                {
                    if ((action == SetupAction.Write || action == SetupAction.Remove) && !status.Parsed)
                    {
                        status.CommandSucceeded = false;
                        status.OutcomeUncertain = true;
                        status.Error = "Presentation mutation returned an invalid response with an unknown outcome. Do not repeat it blindly; click Refresh Presentation Status before any retry.";
                    }
                    return status;
                }
                status.CommandSucceeded = false;
                status.OutcomeUncertain = action == SetupAction.Write || action == SetupAction.Remove;
                status.Error = status.OutcomeUncertain
                    ? "Presentation mutation timed out with an unknown outcome. Do not repeat it blindly; click Refresh Presentation Status before any retry and restart the client if the change was applied."
                    : "Presentation status command timed out. Click Refresh Presentation Status to retry.";
                return status;
            }
        }

        public static ClientProbeStatus ProbeClients(string workspace)
        {
            return new ClientProbeStatus
            {
                OpenCode = ProbeClientVersion(new[] { "opencode.exe", "opencode.cmd", "opencode" }, true, workspace),
                OpenChamber = ProbeClientVersion(new[] { "openchamber.exe", "openchamber.cmd", "openchamber" }, false, workspace),
            };
        }

        public static NodeRuntimeInstaller.InstallResult OpenConfig(string workspace, PresentationStatus status)
        {
            if (string.IsNullOrWhiteSpace(workspace))
            {
                return NodeRuntimeInstaller.InstallResult.Fail("Select a valid OpenCode workspace first.");
            }
            string root;
            try
            {
                root = Path.GetFullPath(workspace);
            }
            catch
            {
                return NodeRuntimeInstaller.InstallResult.Fail("Selected OpenCode workspace path is invalid.");
            }
            var configFile = status != null && (status.ConfigFile == "opencode.json" || status.ConfigFile == "opencode.jsonc")
                ? status.ConfigFile
                : "opencode.jsonc";
            var configPath = Path.Combine(root, configFile);
            if (!File.Exists(configPath))
            {
                return NodeRuntimeInstaller.InstallResult.Fail("Presentation OpenCode config does not exist. Refresh Status or install the plugin first.");
            }
            UnityEditor.EditorUtility.RevealInFinder(configPath);
            return NodeRuntimeInstaller.InstallResult.Ok("Opened the project-scoped presentation config.");
        }

        internal static string BuildArguments(string cliPath, string workspace, SetupAction action)
        {
            return BoundedProcessOperation.BuildArguments(BuildArgumentList(cliPath, workspace, action));
        }

        static List<string> BuildArgumentList(string cliPath, string workspace, SetupAction action)
        {
            return new List<string>
            {
                cliPath,
                "setup",
                "opencode-presentation",
                "--workspace",
                workspace,
                ActionFlag(action),
                "--json",
            };
        }

        internal static PresentationStatus ParseResult(
            string stdout,
            bool processSucceeded,
            string stderr,
            SetupAction? expectedAction = null,
            string expectedWorkspaceHash = null)
        {
            var raw = BoundedProcessOperation.Sanitize(stdout ?? string.Empty);
            PresentationCliPayload payload = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    payload = JsonUtility.FromJson<PresentationCliPayload>(raw);
                }
            }
            catch
            {
                payload = null;
            }
            if (payload == null
                || payload.schema_version != 1
                || payload.setup != "opencode-presentation"
                || !ValidAction(payload.action)
                || string.IsNullOrEmpty(payload.workspace)
                || payload.artifact == null
                || !ValidArtifactPayload(payload.artifact)
                || payload.config == null
                || !ValidConfigPayload(payload.config)
                || payload.changes == null
                || payload.blocked_reasons == null
                || !ValidRecovery(payload.recovery)
                || !HasSingleTopLevelBooleanProperty(raw, "applied")
                || !HasSingleTopLevelBooleanProperty(raw, "restart_required")
                || (expectedAction.HasValue && payload.action != ActionName(expectedAction.Value))
                || !MatchesExpectedWorkspace(payload.workspace, expectedWorkspaceHash))
            {
                return Failure(BoundedMessage(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr));
            }

            var status = new PresentationStatus
            {
                Parsed = true,
                RawJson = raw,
                Workspace = payload.workspace ?? string.Empty,
                ArtifactState = payload.artifact.state ?? "unknown",
                BundledVersion = payload.artifact.bundled_version ?? string.Empty,
                InstalledVersion = payload.artifact.installed_version ?? string.Empty,
                BundledHash = payload.artifact.bundled_hash ?? string.Empty,
                ConfigFile = payload.config.file == "opencode.json" ? "opencode.json" : "opencode.jsonc",
                ConfigState = payload.config.state ?? "unknown",
                OwnedEntries = Math.Max(0, payload.config.owned_entries),
                Recovery = payload.recovery ?? "none",
                Applied = payload.applied,
                RestartRequired = payload.restart_required,
                Changes = payload.changes ?? Array.Empty<string>(),
                BlockedReasons = payload.blocked_reasons ?? Array.Empty<string>(),
            };
            status.CommandSucceeded = processSucceeded && !status.Blocked;
            status.Error = status.CommandSucceeded ? string.Empty : BoundedMessage(string.IsNullOrWhiteSpace(stderr) ? status.Summary() : stderr);
            return status;
        }

        internal static string FormatClientVersion(string output, bool openCode)
        {
            if (!TryExtractVersion(output, out var version))
            {
                return openCode
                    ? "Not detected in the Editor PATH. Bundled plugin supports OpenCode >=2.0.25 <3; active loading and rendering are not verified."
                    : "Not detected in the Editor PATH. OpenChamber v2 requires separate live/visual validation.";
            }
            if (openCode)
            {
                var supported = version.Major == 2 && version.CompareTo(new Version(2, 0, 25)) >= 0;
                return "Detected " + version + (supported ? " - bundled v2 plugin range supported; active loading/rendering not verified." : " - outside bundled v2 plugin range >=2.0.25 <3.");
            }
            return "Detected " + version + " - not yet live-validated for v2 presentation; frontend labels/forms require separate evidence.";
        }

        static string ProbeClientVersion(string[] candidates, bool openCode, string workspace)
        {
            for (var index = 0; index < candidates.Length; index++)
            {
                if (ProcessUtil.TryRun(candidates[index], "--version", Directory.Exists(workspace) ? workspace : null, out var stdout, out var stderr, 5000))
                {
                    return FormatClientVersion(string.IsNullOrWhiteSpace(stdout) ? stderr : stdout, openCode);
                }
            }
            return FormatClientVersion(string.Empty, openCode);
        }

        static bool TryExtractVersion(string output, out Version version)
        {
            version = null;
            var source = BoundedProcessOperation.Sanitize(output ?? string.Empty);
            var tokens = source.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < tokens.Length; index++)
            {
                var token = tokens[index].Trim().TrimStart('v', 'V');
                var suffix = token.IndexOf('-');
                if (suffix >= 0) token = token.Substring(0, suffix);
                if (Version.TryParse(token, out var parsed) && parsed.Build >= 0)
                {
                    version = parsed;
                    return true;
                }
            }
            return false;
        }

        static string ActionFlag(SetupAction action)
        {
            switch (action)
            {
                case SetupAction.Status: return "--status";
                case SetupAction.DryRun: return "--dry-run";
                case SetupAction.Write: return "--write";
                case SetupAction.Remove: return "--remove";
                default: throw new ArgumentOutOfRangeException(nameof(action), action, null);
            }
        }

        static string ActionName(SetupAction action)
        {
            switch (action)
            {
                case SetupAction.Status: return "status";
                case SetupAction.DryRun: return "dry-run";
                case SetupAction.Write: return "write";
                case SetupAction.Remove: return "remove";
                default: throw new ArgumentOutOfRangeException(nameof(action), action, null);
            }
        }

        static bool ValidArtifactPayload(PresentationArtifactPayload artifact)
        {
            return (artifact.state == "absent" || artifact.state == "current" || artifact.state == "outdated" || artifact.state == "modified" || artifact.state == "unknown")
                && !string.IsNullOrEmpty(artifact.bundled_version)
                && !string.IsNullOrEmpty(artifact.bundled_hash)
                && artifact.bundled_hash.StartsWith("sha256:", StringComparison.Ordinal)
                && artifact.bundled_hash.Length == 19;
        }

        static bool ValidConfigPayload(PresentationConfigPayload config)
        {
            return (config.file == "opencode.json" || config.file == "opencode.jsonc")
                && (config.state == "missing" || config.state == "not_configured" || config.state == "configured" || config.state == "duplicate_owned_entries" || config.state == "conflict")
                && config.owned_entries >= 0;
        }

        static bool ValidRecovery(string recovery)
        {
            return recovery == "none" || recovery == "finalized" || recovery == "rolled_back";
        }

        static bool HasSingleTopLevelBooleanProperty(string json, string property)
        {
            var depth = 0;
            var properties = 0;
            var matches = 0;
            for (var index = 0; index < json.Length; index++)
            {
                var character = json[index];
                if (character == '{')
                {
                    depth++;
                    continue;
                }
                if (character == '}')
                {
                    depth--;
                    continue;
                }
                if (character != '"') continue;

                var start = ++index;
                var escaped = false;
                while (index < json.Length)
                {
                    if (json[index] == '\\')
                    {
                        escaped = true;
                        index += 2;
                        continue;
                    }
                    if (json[index] == '"') break;
                    index++;
                }
                var value = index + 1;
                while (value < json.Length && char.IsWhiteSpace(json[value])) value++;
                var isProperty = value < json.Length && json[value] == ':';
                if (depth == 1 && isProperty && escaped) return false;
                if (depth != 1 || !isProperty || index - start != property.Length
                    || string.CompareOrdinal(json, start, property, 0, property.Length) != 0) continue;

                properties++;
                value++;
                while (value < json.Length && char.IsWhiteSpace(json[value])) value++;
                var length = json.IndexOf("true", value, StringComparison.Ordinal) == value ? 4
                    : json.IndexOf("false", value, StringComparison.Ordinal) == value ? 5
                    : 0;
                if (length == 0) continue;
                value += length;
                while (value < json.Length && char.IsWhiteSpace(json[value])) value++;
                if (value < json.Length && (json[value] == ',' || json[value] == '}')) matches++;
            }
            return properties == 1 && matches == 1;
        }

        static bool MatchesExpectedWorkspace(string workspace, string expectedWorkspaceHash)
        {
            if (string.IsNullOrEmpty(expectedWorkspaceHash)) return true;
            try
            {
                var canonicalWorkspace = new CanonicalPathService().NormalizeProjectRoot(workspace).Value;
                return string.Equals(
                    GatewayNetworkModeManager.OpenCodeWorkspaceIdentityHash(canonicalWorkspace),
                    expectedWorkspaceHash,
                    StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        static bool ValidAction(string action)
        {
            return action == "status" || action == "dry-run" || action == "write" || action == "remove";
        }

        static PresentationStatus Failure(string message)
        {
            return new PresentationStatus { Error = BoundedMessage(message) };
        }

        static string BoundedMessage(string value)
        {
            var sanitized = BoundedProcessOperation.Sanitize(value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            if (string.IsNullOrEmpty(sanitized)) return "Presentation setup failed without bounded diagnostics.";
            return sanitized.Length <= 360 ? sanitized : sanitized.Substring(0, 360);
        }

        [Serializable]
        sealed class PresentationCliPayload
        {
            public int schema_version;
            public string setup;
            public string action;
            public string workspace;
            public PresentationArtifactPayload artifact;
            public PresentationConfigPayload config;
            public string[] changes;
            public string[] blocked_reasons;
            public string recovery;
            public bool restart_required;
            public bool applied;
        }

        [Serializable]
        sealed class PresentationArtifactPayload
        {
            public string state;
            public string bundled_version;
            public string installed_version;
            public string bundled_hash;
        }

        [Serializable]
        sealed class PresentationConfigPayload
        {
            public string file;
            public string state;
            public int owned_entries;
        }

        [Serializable]
        sealed class RedactedDiagnosticsPayload
        {
            public int schema_version;
            public string setup;
            public string artifact_state;
            public string bundled_version;
            public string installed_version;
            public string bundled_hash;
            public string config_file;
            public string config_state;
            public int owned_entries;
            public string recovery;
            public bool applied;
            public bool restart_required;
            public string[] changes;
            public string[] blocked_reasons;
        }
    }
}
