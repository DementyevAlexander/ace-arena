using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SGG.UnityMCP.Editor.Bridge;
using UnityEditor;

namespace SGG.UnityMCP.Editor.Setup
{
    public static class GatewayPolicyManager
    {
        public const string ReadonlyProfile = "readonly";
        public const string InspectProfile = "inspect";
        public const string PackageManagementProfile = "package-management";
        public const string TestValidationProfile = "test-validation";
        public const string SafeWritesProfile = "safe-writes";
        public const string RuntimeDebugProfile = "runtime-debug";
        public const string ApiReflectionProfile = "api-reflection";
        public const string UnityDocsProfile = "unity-docs";
        public const string ProfilerReadonlyProfile = "profiler-readonly";
        public const string ProfilerRecordingProfile = "profiler-recording";
        public const string AllowAllProfile = "allow-all";

        public static readonly string[] InspectFeatureFlags = new string[] { "semantic_scene_inspection", "asset_index", "serialize_reference_index", "asset_readonly", "editor_screenshots", "semantic_unity_diff", "unity_docs_local", "knowledge_resources" };
        public static readonly string[] PackageManagementFeatureFlags = new string[] { "package_registry_network" };
        public static readonly string[] TestValidationFeatureFlags = new string[] { "build_test_automation" };
        public static readonly string[] SafeWritesFeatureFlags = new string[] { "asset_readonly", "asset_safe_writes", "editor_safe_writes", "editor_utilities", "semantic_unity_diff" };
        public static readonly string[] RuntimeDebugFeatureFlags = new string[] { "editor_csharp_execution", "runtime_state_debugging", "ugui_interaction_tests", "uitk_interaction_tests" };
        public static readonly string[] ApiReflectionFeatureFlags = new string[] { "unity_api_reflection" };
        public static readonly string[] UnityDocsFeatureFlags = new string[] { "unity_docs_local" };
        public static readonly string[] ProfilerReadonlyFeatureFlags = new string[] { "profiler_readonly" };
        public static readonly string[] ProfilerRecordingFeatureFlags = new string[] { "profiler_recording" };
        public static readonly string[] WorkflowFeatureFlags = new string[] { "sgg_mcp_diagnostics", "sgg_mcp_plans", "sgg_mcp_skills", "sgg_mcp_checkpoints", "sgg_mcp_artifacts", "sgg_mcp_policy" };
        public static readonly string[] UiInspectionFeatureFlags = new string[] { "ugui_inspection", "uitk_inspection", "uitk_validation", "uitk_safe_writes" };
        public static readonly string[] AllFeatureFlags = new string[]
        {
            "semantic_scene_inspection",
            "asset_index",
            "serialize_reference_index",
            "asset_readonly",
            "asset_safe_writes",
            "editor_csharp_execution",
            "editor_utilities",
            "editor_screenshots",
            "game_view_focus_capture",
            "desktop_screenshots",
            "editor_safe_writes",
            "package_registry_network",
            "runtime_state_debugging",
            "unity_api_reflection",
            "unity_docs_local",
            "profiler_readonly",
            "profiler_recording",
            "ugui_inspection",
            "uitk_inspection",
            "ugui_interaction_tests",
            "uitk_interaction_tests",
            "uitk_validation",
            "uitk_safe_writes",
            "semantic_unity_diff",
            "knowledge_resources",
            "build_test_automation",
            "official_unity_mcp_relay",
            "sgg_mcp_diagnostics",
            "sgg_mcp_plans",
            "sgg_mcp_skills",
            "sgg_mcp_checkpoints",
            "sgg_mcp_artifacts",
            "sgg_mcp_policy",
        };

        public static PolicyStatus GetStatus()
        {
            var gatewayStatus = GatewayInstaller.GetStatus();
            var policyExists = File.Exists(gatewayStatus.PolicyPath);
            var configExists = File.Exists(gatewayStatus.ConfigPath);
            var projectPolicyExists = policyExists && PolicyContainsProject(gatewayStatus.PolicyPath, ProjectPaths.ProjectId);
            return new PolicyStatus
            {
                ProjectId = ProjectPaths.ProjectId,
                ConfigPath = gatewayStatus.ConfigPath,
                PolicyPath = gatewayStatus.PolicyPath,
                ExamplePolicyPath = gatewayStatus.ExamplePolicyPath,
                RuntimeReady = gatewayStatus.CanStart,
                UserConfigReady = policyExists && configExists,
                ProjectPolicyReady = projectPolicyExists,
                Message = policyExists && configExists
                    ? projectPolicyExists
                        ? "User policy/config files are ready for project '" + ProjectPaths.ProjectId + "'. Policy changes reload live; use Refresh Health to verify."
                        : "User policy/config files exist, but project '" + ProjectPaths.ProjectId + "' has no policy entry. Create config/policy files or apply a profile, then Refresh Health."
                    : "User policy/config files have not been created yet.",
            };
        }

        public static bool PolicyContainsProject(string policyPath, string projectId)
        {
            if (string.IsNullOrEmpty(policyPath) || string.IsNullOrEmpty(projectId) || !File.Exists(policyPath))
            {
                return false;
            }

            var source = File.ReadAllText(policyPath);
            var projectsIndex = source.IndexOf("\"projects\"", StringComparison.Ordinal);
            if (projectsIndex < 0)
            {
                return false;
            }

            var projectPattern = "\"" + EscapeJsonString(projectId) + "\"";
            return source.IndexOf(projectPattern, projectsIndex, StringComparison.Ordinal) >= 0;
        }

        public static NodeRuntimeInstaller.InstallResult EnsureUserConfig()
        {
            var gatewayStatus = GatewayInstaller.GetStatus();
            if (!gatewayStatus.CanStart)
            {
                return NodeRuntimeInstaller.InstallResult.Fail(gatewayStatus.Message);
            }

            return GatewayInstaller.EnsureUserConfigFiles(gatewayStatus);
        }

        public static NodeRuntimeInstaller.InstallResult OpenGatewayConfigFile()
        {
            var ensureResult = EnsureUserConfig();
            if (!ensureResult.Success)
            {
                return ensureResult;
            }

            EditorUtility.RevealInFinder(UnityMcpPackagePaths.UserGatewayConfigPath);
            return NodeRuntimeInstaller.InstallResult.Ok("Opened Gateway config file at " + UnityMcpPackagePaths.UserGatewayConfigPath);
        }

        public static NodeRuntimeInstaller.InstallResult ApplyProfile(string profile)
        {
            if (string.IsNullOrEmpty(profile))
            {
                return NodeRuntimeInstaller.InstallResult.Fail("Policy profile is required.");
            }

            var gatewayStatus = GatewayInstaller.GetStatus();
            if (!gatewayStatus.CanStart)
            {
                return NodeRuntimeInstaller.InstallResult.Fail(gatewayStatus.Message);
            }

            var nodeStatus = NodeRuntimeInstaller.GetStatus();
            if (!nodeStatus.IsAvailable)
            {
                return NodeRuntimeInstaller.InstallResult.Fail("Node 22+ is not available. Install portable Node first.");
            }

            var ensureResult = GatewayInstaller.EnsureUserConfigFiles(gatewayStatus);
            if (!ensureResult.Success)
            {
                return ensureResult;
            }

            var arguments = ProcessUtil.Quote(gatewayStatus.BundledCliPath)
                + " policy apply-profile"
                + " --policy " + ProcessUtil.Quote(gatewayStatus.PolicyPath)
                + " --from-policy " + ProcessUtil.Quote(gatewayStatus.ExamplePolicyPath)
                + " --project " + ProcessUtil.Quote(ProjectPaths.ProjectId)
                + " --profile " + ProcessUtil.Quote(profile);
            if (profile == AllowAllProfile)
            {
                arguments += " --confirm-risk";
            }

            var pathPrepend = nodeStatus.IsPortable ? Path.GetDirectoryName(nodeStatus.NodePath) : null;
            var ok = ProcessUtil.TryRun(nodeStatus.NodePath, arguments, gatewayStatus.RuntimeRoot, out var stdout, out var stderr, 30000, pathPrepend);
            if (!ok)
            {
                return NodeRuntimeInstaller.InstallResult.Fail("Policy profile failed: " + TrimForDialog(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr));
            }

            return NodeRuntimeInstaller.InstallResult.Ok("Applied policy profile '" + profile + "' for project '" + ProjectPaths.ProjectId + "'. The running Gateway reloads valid policy live; use Refresh Health to verify.");
        }

        public static NodeRuntimeInstaller.InstallResult EnableFeatureProfile(string profile)
        {
            return EnableFeatureFlags("profile '" + profile + "'", FeatureFlagsForProfile(profile));
        }

        public static NodeRuntimeInstaller.InstallResult EnableAllFeatureFlags()
        {
            return EnableFeatureFlags("all known feature flags", AllFeatureFlags);
        }

        public static NodeRuntimeInstaller.InstallResult EnableFeatureFlags(string label, string[] featureFlags)
        {
            if (featureFlags == null || featureFlags.Length == 0)
            {
                return NodeRuntimeInstaller.InstallResult.Ok("No feature flags are required for " + label + ".");
            }

            var ensureResult = EnsureUserConfig();
            if (!ensureResult.Success)
            {
                return ensureResult;
            }

            try
            {
                var configPath = UnityMcpPackagePaths.UserGatewayConfigPath;
                using (AcquireGatewayConfigWriteLock(configPath))
                {
                    var source = File.ReadAllText(configPath);
                    File.WriteAllText(configPath, UpsertFeatureFlags(source, featureFlags, true));
                }
                return NodeRuntimeInstaller.InstallResult.Ok("Enabled feature flags for " + label + ": " + string.Join(", ", featureFlags) + ". Restart Gateway to reload the config.");
            }
            catch (Exception exception)
            {
                return NodeRuntimeInstaller.InstallResult.Fail("Could not update feature flags: " + exception.Message);
            }
        }

        public static string[] FeatureFlagsForProfile(string profile)
        {
            switch (profile)
            {
                case InspectProfile:
                    return InspectFeatureFlags;
                case PackageManagementProfile:
                    return PackageManagementFeatureFlags;
                case TestValidationProfile:
                    return TestValidationFeatureFlags;
                case SafeWritesProfile:
                    return SafeWritesFeatureFlags;
                case RuntimeDebugProfile:
                    return RuntimeDebugFeatureFlags;
                case ApiReflectionProfile:
                    return ApiReflectionFeatureFlags;
                case UnityDocsProfile:
                    return UnityDocsFeatureFlags;
                case ProfilerReadonlyProfile:
                    return ProfilerReadonlyFeatureFlags;
                case ProfilerRecordingProfile:
                    return ProfilerRecordingFeatureFlags;
                case AllowAllProfile:
                    return AllFeatureFlags;
                default:
                    return new string[0];
            }
        }

        public static NodeRuntimeInstaller.InstallResult OpenPolicyFile()
        {
            var ensureResult = EnsureUserConfig();
            if (!ensureResult.Success)
            {
                return ensureResult;
            }

            EditorUtility.RevealInFinder(UnityMcpPackagePaths.UserPolicyConfigPath);
            return NodeRuntimeInstaller.InstallResult.Ok("Opened policy file at " + UnityMcpPackagePaths.UserPolicyConfigPath);
        }

        public static bool GetAutoAllowAsk()
        {
            var status = GetStatus();
            if (string.IsNullOrEmpty(status.ConfigPath) || !File.Exists(status.ConfigPath))
            {
                return false;
            }

            var source = File.ReadAllText(status.ConfigPath);
            return Regex.IsMatch(source, "\\\"auto_allow_ask\\\"\\s*:\\s*true", RegexOptions.None, TimeSpan.FromMilliseconds(250));
        }

        public static NodeRuntimeInstaller.InstallResult SetAutoAllowAsk(bool enabled)
        {
            var ensureResult = EnsureUserConfig();
            if (!ensureResult.Success)
            {
                return ensureResult;
            }

            try
            {
                var configPath = UnityMcpPackagePaths.UserGatewayConfigPath;
                using (AcquireGatewayConfigWriteLock(configPath))
                {
                    var source = File.ReadAllText(configPath);
                    File.WriteAllText(configPath, UpsertAutoAllowAsk(source, enabled));
                }
                return NodeRuntimeInstaller.InstallResult.Ok("Set approvals.auto_allow_ask=" + (enabled ? "true" : "false") + ". Restart Gateway to reload the config.");
            }
            catch (Exception exception)
            {
                return NodeRuntimeInstaller.InstallResult.Fail("Could not update approvals.auto_allow_ask: " + exception.Message);
            }
        }

        const string AutoAllowKnownUnityPopupsKey = "auto_allow_known_unity_popups";
        const int MaxPopupApprovalConfigChars = 1024 * 1024;

        public static bool GetAutoAllowKnownUnityPopups()
        {
            try
            {
                var configPath = UnityMcpPackagePaths.UserGatewayConfigPath;
                if (!File.Exists(configPath) || new FileInfo(configPath).Length > MaxPopupApprovalConfigChars)
                {
                    return false;
                }

                return ReadAutoAllowKnownUnityPopups(File.ReadAllText(configPath));
            }
            catch
            {
                // Missing, unreadable or invalid config never grants popup approval.
                return false;
            }
        }

        public static NodeRuntimeInstaller.InstallResult SetAutoAllowKnownUnityPopups(bool enabled)
        {
            try
            {
                var configPath = UnityMcpPackagePaths.UserGatewayConfigPath;
                if (!File.Exists(configPath))
                {
                    return NodeRuntimeInstaller.InstallResult.Fail("Gateway config is missing. Create Config/Policy Files first.");
                }

                using (AcquireGatewayConfigWriteLock(configPath))
                {
                    if (new FileInfo(configPath).Length > MaxPopupApprovalConfigChars)
                    {
                        return NodeRuntimeInstaller.InstallResult.Fail("Gateway config exceeds the popup approval settings size limit.");
                    }

                    var source = File.ReadAllText(configPath);
                    var updated = UpsertAutoAllowKnownUnityPopups(source, enabled);
                    if (Encoding.UTF8.GetByteCount(updated) > MaxPopupApprovalConfigChars)
                    {
                        return NodeRuntimeInstaller.InstallResult.Fail("Updated Gateway config exceeds the popup approval settings size limit.");
                    }

                    AtomicWritePopupApprovalConfigLocked(configPath, source, updated);
                }

                return NodeRuntimeInstaller.InstallResult.Ok("Set approvals.auto_allow_known_unity_popups=" + (enabled ? "true" : "false") + ". On a Gateway runtime supporting live popup settings, saved enable/disable changes apply on the next request without restart and are rechecked before response issuance. Check diagnostics.status popup_response_approval.enabled for the running state.");
            }
            catch (Exception exception)
            {
                return NodeRuntimeInstaller.InstallResult.Fail("Could not update approvals.auto_allow_known_unity_popups: " + exception.Message);
            }
        }

        internal static bool ReadAutoAllowKnownUnityPopups(string source)
        {
            try
            {
                var root = PopupApprovalConfigParser.Parse(source);
                var approvals = PopupApprovalsObject(root);
                var setting = PopupApprovalSetting(approvals);
                return setting != null && source[setting.Start] == 't';
            }
            catch (FormatException)
            {
                return false;
            }
        }

        internal static string UpsertAutoAllowKnownUnityPopups(string source, bool enabled)
        {
            var root = PopupApprovalConfigParser.Parse(source);
            var approvals = PopupApprovalsObject(root);
            var setting = PopupApprovalSetting(approvals);
            var value = enabled ? "true" : "false";
            string updated;
            if (setting != null)
            {
                updated = source.Substring(0, setting.Start) + value + source.Substring(setting.End);
            }
            else
            {
                var property = "\"" + AutoAllowKnownUnityPopupsKey + "\": " + value;
                updated = approvals != null
                    ? InsertPopupApprovalProperty(source, approvals, property)
                    : InsertPopupApprovalProperty(source, root, "\"approvals\": { \"dir\": \"%LOCALAPPDATA%/SGG/UnityMcpGateway/approvals\", " + property + " }");
            }

            if (updated.Length > MaxPopupApprovalConfigChars)
            {
                throw new FormatException("Updated Gateway config exceeds the popup approval settings size limit.");
            }

            return updated;
        }

        static PopupConfigValue PopupApprovalsObject(PopupConfigValue root)
        {
            if (!root.Members.TryGetValue("approvals", out var approvals))
            {
                return null;
            }

            if (approvals.Members == null)
            {
                throw new FormatException("Gateway config approvals must be an object.");
            }

            return approvals;
        }

        static PopupConfigValue PopupApprovalSetting(PopupConfigValue approvals)
        {
            if (approvals == null || !approvals.Members.TryGetValue(AutoAllowKnownUnityPopupsKey, out var setting))
            {
                return null;
            }

            if (!setting.IsBoolean)
            {
                throw new FormatException("Gateway config approvals.auto_allow_known_unity_popups must be a boolean.");
            }

            return setting;
        }

        static string InsertPopupApprovalProperty(string source, PopupConfigValue owner, string property)
        {
            // Insert after the last value, leaving every existing token and whitespace intact.
            var insertion = owner.End - 1;
            while (insertion > owner.Start + 1 && PopupApprovalConfigParser.IsWhitespace(source[insertion - 1])) insertion--;
            var prefix = owner.Members.Count == 0 ? string.Empty : ",";
            return source.Substring(0, insertion) + prefix + " " + property + source.Substring(insertion);
        }

        internal static FileStream AcquireGatewayConfigWriteLock(string configPath)
        {
            // A stable sidecar serializes the config writers in this manager across Editor processes.
            // Leave it in place: deleting a lock file permits different handles to lock different files.
            var lockPath = configPath + ".settings-write.lock";
            if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Gateway config writer lock must not be a symbolic link or reparse point.");
            }

            // Contention fails closed instead of blocking the Editor UI or overwriting another writer.
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        internal static void AtomicWritePopupApprovalConfig(string configPath, string original, string updated, Action beforeReplacement = null)
        {
            using (AcquireGatewayConfigWriteLock(configPath))
            {
                AtomicWritePopupApprovalConfigLocked(configPath, original, updated, beforeReplacement);
            }
        }

        static void AtomicWritePopupApprovalConfigLocked(string configPath, string original, string updated, Action beforeReplacement = null)
        {
            if ((File.GetAttributes(configPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Gateway config must not be a symbolic link or reparse point.");
            }

            var tempPath = configPath + ".tmp-" + Guid.NewGuid().ToString("N");
            var tempCreated = false;
            try
            {
                var bytes = new UTF8Encoding(false, true).GetBytes(updated);
                if (bytes.Length > MaxPopupApprovalConfigChars)
                {
                    throw new IOException("Updated Gateway config exceeds the popup approval settings size limit.");
                }
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    tempCreated = true;
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                // On Windows this denies in-place writes, including already-open write handles,
                // from comparison through replacement. Delete sharing is needed by File.Replace.
                // It cannot prevent an uncoordinated external rename/replacement of the path;
                // this is not universal CAS against hostile external writers.
                using (var destination = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    if (destination.Length > MaxPopupApprovalConfigChars
                        || (File.GetAttributes(configPath) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException("Gateway config is oversized or became a symbolic link/reparse point while saving.");
                    }

                    string current;
                    using (var reader = new StreamReader(destination, Encoding.UTF8, true, 4096, true))
                    {
                        current = reader.ReadToEnd();
                    }
                    if (!string.Equals(current, original, StringComparison.Ordinal))
                    {
                        throw new IOException("Gateway config changed while saving. Refresh and try again.");
                    }
                    // Validate even no-op requests, but do not replace or re-encode an unchanged file.
                    if (string.Equals(original, updated, StringComparison.Ordinal)) return;

                    // Deterministic test seam at the previously unprotected comparison/replace boundary.
                    beforeReplacement?.Invoke();
                    // No copy/overwrite fallback: a failed atomic replacement leaves the original intact.
                    File.Replace(tempPath, configPath, null);
                }
            }
            finally
            {
                if (tempCreated && File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        sealed class PopupConfigValue
        {
            public int Start;
            public int End;
            public bool IsBoolean;
            public Dictionary<string, PopupConfigValue> Members;
        }

        // Strict JSON with decoded member names and source spans. No serializer dependency or
        // global regex replacement; unknown values (including number spelling) are preserved.
        sealed class PopupApprovalConfigParser
        {
            readonly string m_source;
            int m_cursor;

            PopupApprovalConfigParser(string source)
            {
                m_source = source;
            }

            public static PopupConfigValue Parse(string source)
            {
                if (string.IsNullOrEmpty(source) || source.Length > MaxPopupApprovalConfigChars)
                {
                    throw Invalid();
                }

                var parser = new PopupApprovalConfigParser(source);
                var root = parser.ReadValue(0);
                parser.SkipWhitespace();
                if (root.Members == null || parser.m_cursor != source.Length) throw Invalid();
                return root;
            }

            PopupConfigValue ReadValue(int depth)
            {
                SkipWhitespace();
                if (depth > 64 || m_cursor >= m_source.Length) throw Invalid();
                var value = new PopupConfigValue { Start = m_cursor };
                var character = m_source[m_cursor];
                if (character == '{')
                {
                    m_cursor++;
                    value.Members = new Dictionary<string, PopupConfigValue>(StringComparer.Ordinal);
                    SkipWhitespace();
                    if (!Consume('}'))
                    {
                        do
                        {
                            SkipWhitespace();
                            var name = ReadString();
                            SkipWhitespace();
                            if (!Consume(':') || value.Members.ContainsKey(name)) throw Invalid();
                            value.Members.Add(name, ReadValue(depth + 1));
                            SkipWhitespace();
                            if (Consume('}')) break;
                            if (!Consume(',')) throw Invalid();
                        } while (true);
                    }
                }
                else if (character == '[')
                {
                    m_cursor++;
                    SkipWhitespace();
                    if (!Consume(']'))
                    {
                        do
                        {
                            ReadValue(depth + 1);
                            SkipWhitespace();
                            if (Consume(']')) break;
                            if (!Consume(',')) throw Invalid();
                        } while (true);
                    }
                }
                else if (character == '"') ReadString();
                else if (character == 't' || character == 'f')
                {
                    ReadLiteral(character == 't' ? "true" : "false");
                    value.IsBoolean = true;
                }
                else if (character == 'n') ReadLiteral("null");
                else ReadNumber();
                value.End = m_cursor;
                return value;
            }

            string ReadString()
            {
                if (!Consume('"')) throw Invalid();
                var builder = new StringBuilder();
                while (m_cursor < m_source.Length)
                {
                    var character = m_source[m_cursor++];
                    if (character == '"') return builder.ToString();
                    if (character < 0x20) throw Invalid();
                    if (character != '\\')
                    {
                        builder.Append(character);
                        continue;
                    }

                    if (m_cursor >= m_source.Length) throw Invalid();
                    switch (m_source[m_cursor++])
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            var code = 0;
                            for (var index = 0; index < 4; index++)
                            {
                                if (m_cursor >= m_source.Length) throw Invalid();
                                var digit = m_source[m_cursor++];
                                var hex = digit >= '0' && digit <= '9' ? digit - '0'
                                    : digit >= 'a' && digit <= 'f' ? digit - 'a' + 10
                                    : digit >= 'A' && digit <= 'F' ? digit - 'A' + 10 : -1;
                                if (hex < 0) throw Invalid();
                                code = code * 16 + hex;
                            }
                            builder.Append((char)code);
                            break;
                        default: throw Invalid();
                    }
                }

                throw Invalid();
            }

            void ReadNumber()
            {
                Consume('-');
                if (!Consume('0'))
                {
                    if (m_cursor >= m_source.Length || m_source[m_cursor] < '1' || m_source[m_cursor] > '9') throw Invalid();
                    ReadDigits();
                }
                if (Consume('.')) ReadDigits();
                if (Consume('e') || Consume('E'))
                {
                    if (!Consume('+')) Consume('-');
                    ReadDigits();
                }
            }

            void ReadDigits()
            {
                var start = m_cursor;
                while (m_cursor < m_source.Length && m_source[m_cursor] >= '0' && m_source[m_cursor] <= '9') m_cursor++;
                if (m_cursor == start) throw Invalid();
            }

            void ReadLiteral(string literal)
            {
                for (var index = 0; index < literal.Length; index++)
                {
                    if (!Consume(literal[index])) throw Invalid();
                }
            }

            bool Consume(char character)
            {
                if (m_cursor >= m_source.Length || m_source[m_cursor] != character) return false;
                m_cursor++;
                return true;
            }

            void SkipWhitespace()
            {
                while (m_cursor < m_source.Length && IsWhitespace(m_source[m_cursor])) m_cursor++;
            }

            public static bool IsWhitespace(char character)
            {
                return character == ' ' || character == '\t' || character == '\r' || character == '\n';
            }

            static FormatException Invalid()
            {
                return new FormatException("Gateway config must be a bounded, valid JSON object without duplicate properties.");
            }
        }

        public static NodeRuntimeInstaller.InstallResult CopyProjectPolicySnippet()
        {
            GUIUtilityBridge.CopyText(ProjectPolicySnippet(ProjectPaths.ProjectId));
            return NodeRuntimeInstaller.InstallResult.Ok("Copied policy snippet for project '" + ProjectPaths.ProjectId + "'.");
        }

        static string ProjectPolicySnippet(string projectId)
        {
            return "\"" + projectId + "\": {\n"
                + "  \"allow\": [\n"
                + "    \"logs.*\",\n"
                + "    \"compile.status\",\n"
                + "    \"package.list\",\n"
                + "    \"package.info\",\n"
                + "    \"package.operation.status\",\n"
                + "    \"build.settings\",\n"
                + "    \"build.profile.list\",\n"
                + "    \"script.read\",\n"
                + "    \"scene.loaded\"\n"
                + "  ],\n"
                + "  \"ask\": [\n"
                + "    \"package.install\",\n"
                + "    \"package.install.git\",\n"
                + "    \"package.remove\",\n"
                + "    \"asset.folder.create\",\n"
                + "    \"script.write\",\n"
                + "    \"gameobject.*\",\n"
                + "    \"component.*\",\n"
                + "    \"material.*\"\n"
                + "  ],\n"
                + "  \"deny\": [\n"
                + "    \"shell.*\",\n"
                + "    \"process.*\",\n"
                + "    \"network.*\"\n"
                + "  ]\n"
                + "}";
        }

        static string TrimForDialog(string value)
        {
            var text = string.IsNullOrWhiteSpace(value) ? "No output" : value.Trim();
            const int maxLength = 1200;
            return text.Length <= maxLength ? text : text.Substring(0, maxLength) + "\n...";
        }

        static string UpsertAutoAllowAsk(string source, bool enabled)
        {
            var value = "\"auto_allow_ask\": " + (enabled ? "true" : "false");
            if (Regex.IsMatch(source, "\\\"auto_allow_ask\\\"\\s*:\\s*(true|false)", RegexOptions.None, TimeSpan.FromMilliseconds(250)))
            {
                return Regex.Replace(source, "\\\"auto_allow_ask\\\"\\s*:\\s*(true|false)", value, RegexOptions.None, TimeSpan.FromMilliseconds(250));
            }

            var approvalsIndex = source.IndexOf("\"approvals\"", StringComparison.Ordinal);
            if (approvalsIndex >= 0)
            {
                var objectStart = source.IndexOf('{', approvalsIndex);
                var objectEnd = FindObjectEnd(source, objectStart);
                if (objectStart >= 0 && objectEnd > objectStart)
                {
                    var beforeClose = source.Substring(0, objectEnd).TrimEnd();
                    var suffixWhitespace = source.Substring(beforeClose.Length, objectEnd - beforeClose.Length);
                    var comma = beforeClose.EndsWith("{", StringComparison.Ordinal) ? string.Empty : ",";
                    return beforeClose + comma + "\n    " + value + suffixWhitespace + source.Substring(objectEnd);
                }
            }

            var rootEnd = source.LastIndexOf('}');
            if (rootEnd < 0)
            {
                return source;
            }

            var rootPrefix = source.Substring(0, rootEnd).TrimEnd();
            var rootComma = rootPrefix.EndsWith("{", StringComparison.Ordinal) ? string.Empty : ",";
            return rootPrefix + rootComma + "\n  \"approvals\": {\n"
                + "    \"dir\": \"%LOCALAPPDATA%/SGG/UnityMcpGateway/approvals\",\n"
                + "    \"default_ttl_ms\": 300000,\n"
                + "    " + value + "\n"
                + "  }\n"
                + source.Substring(rootEnd);
        }

        static string UpsertFeatureFlags(string source, string[] featureFlags, bool enabled)
        {
            var featuresIndex = source.IndexOf("\"features\"", StringComparison.Ordinal);
            if (featuresIndex >= 0)
            {
                var objectStart = source.IndexOf('{', featuresIndex);
                var objectEnd = FindObjectEnd(source, objectStart);
                if (objectStart >= 0 && objectEnd > objectStart)
                {
                    var order = new List<string>();
                    var values = ReadFeatureFlagValues(source.Substring(objectStart + 1, objectEnd - objectStart - 1), order);
                    AddOrSetFeatureFlags(featureFlags, enabled, order, values);
                    return source.Substring(0, objectStart) + FeatureObjectBlock(order, values) + source.Substring(objectEnd + 1);
                }
            }

            var rootEnd = source.LastIndexOf('}');
            if (rootEnd < 0)
            {
                return source;
            }

            var newOrder = new List<string>();
            var newValues = new Dictionary<string, bool>(StringComparer.Ordinal);
            AddOrSetFeatureFlags(featureFlags, enabled, newOrder, newValues);
            var rootPrefix = source.Substring(0, rootEnd).TrimEnd();
            var rootComma = rootPrefix.EndsWith("{", StringComparison.Ordinal) ? string.Empty : ",";
            return rootPrefix + rootComma + "\n  \"features\": " + FeatureObjectBlock(newOrder, newValues) + "\n" + source.Substring(rootEnd);
        }

        static Dictionary<string, bool> ReadFeatureFlagValues(string source, List<string> order)
        {
            var values = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (Match match in Regex.Matches(source, "\\\"([A-Za-z0-9_]+)\\\"\\s*:\\s*(true|false)", RegexOptions.None, TimeSpan.FromMilliseconds(250)))
            {
                var flag = match.Groups[1].Value;
                if (values.ContainsKey(flag))
                {
                    continue;
                }

                order.Add(flag);
                values[flag] = match.Groups[2].Value == "true";
            }

            return values;
        }

        static void AddOrSetFeatureFlags(string[] featureFlags, bool enabled, List<string> order, Dictionary<string, bool> values)
        {
            for (var index = 0; index < featureFlags.Length; index++)
            {
                var flag = featureFlags[index];
                if (string.IsNullOrEmpty(flag))
                {
                    continue;
                }

                if (!values.ContainsKey(flag))
                {
                    order.Add(flag);
                }

                values[flag] = enabled;
            }
        }

        static string FeatureObjectBlock(List<string> order, Dictionary<string, bool> values)
        {
            var builder = new StringBuilder();
            builder.Append("{\n");
            for (var index = 0; index < order.Count; index++)
            {
                var flag = order[index];
                builder.Append("    \"").Append(EscapeJsonString(flag)).Append("\": ").Append(values[flag] ? "true" : "false");
                if (index < order.Count - 1)
                {
                    builder.Append(',');
                }

                builder.Append('\n');
            }

            builder.Append("  }");
            return builder.ToString();
        }

        static int FindObjectEnd(string source, int objectStart)
        {
            if (objectStart < 0 || objectStart >= source.Length || source[objectStart] != '{')
            {
                return -1;
            }

            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var index = objectStart; index < source.Length; index++)
            {
                var character = source[index];
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
                    depth++;
                }
                else if (character == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return index;
                    }
                }
            }

            return -1;
        }

        static string EscapeJsonString(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        public sealed class PolicyStatus
        {
            public string ProjectId = string.Empty;
            public string ConfigPath = string.Empty;
            public string PolicyPath = string.Empty;
            public string ExamplePolicyPath = string.Empty;
            public bool RuntimeReady;
            public bool UserConfigReady;
            public bool ProjectPolicyReady;
            public string Message = string.Empty;
        }
    }

    static class GUIUtilityBridge
    {
        public static void CopyText(string text)
        {
            UnityEngine.GUIUtility.systemCopyBuffer = text;
        }
    }
}
