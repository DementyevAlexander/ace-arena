using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Paths;
using UnityEditor;

namespace SGG.UnityMCP.Editor.Setup
{
    public static class GatewayNetworkModeManager
    {
        public const string ModeWindowsStdio = "windows-stdio";
        public const string ModeRemoteHttp = "remote-http";
        public const string ModeWindowsStdioLabel = "WSL via Windows stdio shim (recommended)";
        public const string ModeRemoteHttpLabel = "WSL via Remote HTTP";
        internal const int OpenCodeMcpCallTimeoutMs = 3900000;
        internal const int OpenCodeMcpStartupTimeoutMs = 30000;
        internal const int OpenCodeMcpCatalogTimeoutMs = 30000;
        internal const int OpenCodeMcpDefaultExecutionTimeoutMs = 43200000;
        const string McpTokenEnvName = "SGG_UNITY_MCP_TOKEN";
        const string OpenCodeWorkspacePrefsPrefix = "SGG.UnityMCP.McpWindow.OpenCodeWorkspaceRoot.";
        const string OpenCodeClientRootHashDomain = "sgg-unity-mcp-client-root-v1\0";
        static string s_openCodeGitProjectRoot;
        static string s_openCodeGitRoot;
        static bool s_openCodeGitRootResolved;

        public static NetworkStatus GetStatus(string selectedMode)
        {
            var gatewayStatus = GatewayInstaller.GetStatus();
            TryStripJsonComments(File.Exists(gatewayStatus.ConfigPath) ? File.ReadAllText(gatewayStatus.ConfigPath) : string.Empty, out var configSource);
            var validConfigRoot = TryGetSingleRootObject(configSource, out var configRootStart, out var configRootEnd);
            var mcpConfigStart = -1;
            var mcpConfigEnd = -1;
            var hasMcpConfig = validConfigRoot && TryFindDirectObject(configSource, configRootStart, configRootEnd, "mcp", out mcpConfigStart, out mcpConfigEnd);
            var host = hasMcpConfig ? ReadDirectString(configSource, mcpConfigStart, mcpConfigEnd, "host", "127.0.0.1") : "127.0.0.1";
            var port = hasMcpConfig ? ReadDirectInt(configSource, mcpConfigStart, mcpConfigEnd, "port", 27182) : 27182;
            var allowNonLoopback = hasMcpConfig && ReadDirectBool(configSource, mcpConfigStart, mcpConfigEnd, "allow_non_loopback", false);
            var allowedOrigins = hasMcpConfig ? ReadDirectArraySummary(configSource, mcpConfigStart, mcpConfigEnd, "allowed_origins") : string.Empty;
            var tokenEnv = hasMcpConfig ? ReadDirectString(configSource, mcpConfigStart, mcpConfigEnd, "token_env", McpTokenEnvName) : McpTokenEnvName;
            var tokenPresent = !string.IsNullOrEmpty(tokenEnv) && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(tokenEnv));
            var normalizedMode = NormalizeMode(selectedMode);
            var windowsHostIp = normalizedMode == ModeRemoteHttp ? DetectWindowsHostIp() : string.Empty;
            var mcpUrl = normalizedMode == ModeRemoteHttp ? "http://" + (string.IsNullOrEmpty(windowsHostIp) ? "<windows-host-ip>" : windowsHostIp) + ":" + port + "/mcp" : "http://127.0.0.1:" + port + "/mcp";
            var loopbackBind = IsLoopbackHost(host);
            var bindSecurityReady = hasMcpConfig && IsBindSecurityReady(host, allowNonLoopback, tokenPresent, allowedOrigins);
            OpenCodeConfigPathResolver.Result openCodeTarget;
            string openCodeTargetError;
            var openCodeTargetAvailable = TryResolveOpenCodeTarget(out openCodeTarget, out openCodeTargetError);

            return new NetworkStatus
            {
                SelectedMode = normalizedMode,
                ConfigPath = gatewayStatus.ConfigPath,
                OpenCodeWorkspaceRoot = openCodeTargetAvailable ? openCodeTarget.WorkspaceRoot : string.Empty,
                OpenCodeWorkspaceHash = openCodeTargetAvailable ? OpenCodeWorkspaceIdentityHash(openCodeTarget.CanonicalWorkspaceRoot) : string.Empty,
                OpenCodePath = openCodeTargetAvailable && !openCodeTarget.HasConflict ? openCodeTarget.ConfigPath : string.Empty,
                OpenCodeJsoncPath = openCodeTargetAvailable ? openCodeTarget.JsoncPath : string.Empty,
                OpenCodeJsonPath = openCodeTargetAvailable ? openCodeTarget.JsonPath : string.Empty,
                OpenCodeTargetSource = openCodeTargetAvailable ? openCodeTarget.Source.ToString() : string.Empty,
                OpenCodeTargetState = openCodeTargetAvailable ? openCodeTarget.State.ToString() : string.Empty,
                OpenCodeTargetAvailable = openCodeTargetAvailable,
                OpenCodeConfigConflict = openCodeTargetAvailable && openCodeTarget.HasConflict,
                OpenCodeRequiresConfirmation = openCodeTargetAvailable && openCodeTarget.RequiresConfirmation,
                OpenCodeNestedProject = openCodeTargetAvailable && openCodeTarget.IsNestedProject,
                OpenCodeSavedWorkspaceRejected = openCodeTargetAvailable && openCodeTarget.SavedWorkspaceRejected,
                OpenCodeTargetMessage = DescribeOpenCodeTarget(openCodeTarget, openCodeTargetError),
                Host = host,
                Port = port,
                AllowNonLoopback = allowNonLoopback,
                AllowedOrigins = allowedOrigins,
                TokenEnv = tokenEnv,
                TokenPresent = tokenPresent,
                LoopbackBind = loopbackBind,
                BindSecurityReady = bindSecurityReady,
                WindowsHostIp = windowsHostIp,
                McpUrl = mcpUrl,
                OpenCodeConfigReady = openCodeTargetAvailable
                    && IsOpenCodeTargetReady(openCodeTarget, normalizedMode, mcpUrl, tokenEnv),
                Message = MessageFor(host, allowNonLoopback, tokenPresent, allowedOrigins),
            };
        }

        static bool TryResolveOpenCodeTarget(out OpenCodeConfigPathResolver.Result target, out string error)
        {
            try
            {
                var projectRoot = ProjectPaths.ProjectRoot;
                var savedWorkspaceRoot = SavedOpenCodeWorkspaceRoot(projectRoot);
                if (!s_openCodeGitRootResolved || !string.Equals(s_openCodeGitProjectRoot, projectRoot, StringComparison.Ordinal))
                {
                    s_openCodeGitProjectRoot = projectRoot;
                    s_openCodeGitRoot = OpenCodeConfigPathResolver.DiscoverGitRoot(projectRoot);
                    s_openCodeGitRootResolved = true;
                }

                target = OpenCodeConfigPathResolver.ResolveWithKnownGitRoot(projectRoot, null, savedWorkspaceRoot, s_openCodeGitRoot);
                error = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                target = null;
                error = exception.Message;
                return false;
            }
        }

        public static NodeRuntimeInstaller.InstallResult SetOpenCodeWorkspaceRoot(string workspaceRoot)
        {
            try
            {
                var projectRoot = ProjectPaths.ProjectRoot;
                var candidate = ValidateOpenCodeWorkspaceSelection(projectRoot, workspaceRoot);

                EditorPrefs.SetString(OpenCodeWorkspacePrefsKey(projectRoot), candidate.WorkspaceRoot);
                return NodeRuntimeInstaller.InstallResult.Ok("Saved OpenCode workspace for this Unity project: " + candidate.WorkspaceRoot);
            }
            catch (Exception exception)
            {
                return NodeRuntimeInstaller.InstallResult.Fail("OpenCode workspace was not saved: " + exception.Message);
            }
        }

        internal static OpenCodeConfigPathResolver.Result ValidateOpenCodeWorkspaceSelection(string projectRoot, string workspaceRoot)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot))
            {
                throw new ArgumentException("OpenCode workspace root is required. Use Auto to clear the saved override.", nameof(workspaceRoot));
            }

            var candidate = OpenCodeConfigPathResolver.ResolveWithKnownGitRoot(projectRoot, workspaceRoot, null, null);
            if (!Directory.Exists(candidate.WorkspaceRoot))
            {
                throw new DirectoryNotFoundException("Selected OpenCode workspace directory does not exist: " + candidate.WorkspaceRoot);
            }

            return candidate;
        }

        public static NodeRuntimeInstaller.InstallResult ResetOpenCodeWorkspaceRoot()
        {
            try
            {
                EditorPrefs.DeleteKey(OpenCodeWorkspacePrefsKey(ProjectPaths.ProjectRoot));
                InvalidateOpenCodeGitRootCache();
                return NodeRuntimeInstaller.InstallResult.Ok("OpenCode workspace override cleared. Automatic Git/fallback resolution is active.");
            }
            catch (Exception exception)
            {
                return NodeRuntimeInstaller.InstallResult.Fail("OpenCode workspace override could not be cleared: " + exception.Message);
            }
        }

        public static void InvalidateOpenCodeGitRootCache()
        {
            s_openCodeGitProjectRoot = null;
            s_openCodeGitRoot = null;
            s_openCodeGitRootResolved = false;
        }

        internal static string SavedOpenCodeWorkspaceRoot(string projectRoot)
        {
            return EditorPrefs.GetString(OpenCodeWorkspacePrefsKey(projectRoot), string.Empty);
        }

        internal static string OpenCodeWorkspacePrefsKey(string projectRoot)
        {
            var canonicalRoot = new CanonicalPathService().NormalizeProjectRoot(projectRoot).Value;
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(canonicalRoot));
                return OpenCodeWorkspacePrefsPrefix + BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        internal static string OpenCodeWorkspaceIdentityHash(string canonicalWorkspaceRoot)
        {
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(OpenCodeClientRootHashDomain + canonicalWorkspaceRoot));
                return "sha256:" + BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        static string DescribeOpenCodeTarget(OpenCodeConfigPathResolver.Result target, string error)
        {
            if (target == null)
            {
                return "Unavailable - " + (string.IsNullOrWhiteSpace(error) ? "OpenCode config target could not be resolved." : error);
            }

            if (target.HasConflict)
            {
                if (target.HasHiddenLayer)
                {
                    return HiddenLayerConflictMessage(target);
                }
                return "Conflict - both config files exist; choose one manually before setup: " + target.JsoncPath + " | " + target.JsonPath;
            }

            var source = target.Source == OpenCodeConfigPathResolver.WorkspaceSource.GitWorktree
                ? "Git worktree"
                : target.Source == OpenCodeConfigPathResolver.WorkspaceSource.UnityProjectFallback
                    ? "Unity project fallback"
                    : target.Source.ToString();
            var details = source + " - " + target.ConfigPath;
            if (target.IsNestedProject) details += " (nested Unity project)";
            if (target.RequiresConfirmation) details += " (confirmation required before write)";
            if (target.SavedWorkspaceRejected) details += " (saved workspace rejected)";
            details += " (selected-file check only; ancestor/global/inline config is not verified)";
            return details;
        }

        static string HiddenLayerConflictMessage(OpenCodeConfigPathResolver.Result target)
        {
            return "Conflict - a local .opencode config layer may replace the entire same-named MCP server. Setup will not choose, merge, rename or delete layers: "
                + target.JsoncPath + " | " + target.JsonPath + " | " + target.HiddenJsoncPath + " | " + target.HiddenJsonPath;
        }

        internal static bool IsOpenCodeTargetReady(OpenCodeConfigPathResolver.Result target, string mode, string mcpUrl, string tokenEnv = McpTokenEnvName)
        {
            return target != null && !target.HasConflict && IsOpenCodeConfigReady(target.ConfigPath, mode, mcpUrl, tokenEnv);
        }

        internal static bool IsOpenCodeConfigReady(string path, string mode, string mcpUrl, string tokenEnv = McpTokenEnvName)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024)
                {
                    return false;
                }

                if (!TryStripJsonComments(File.ReadAllText(path), out var source))
                {
                    return false;
                }
                if (!TryGetSingleRootObject(source, out var rootStart, out var rootEnd)
                    || !TryFindDirectObject(source, rootStart, rootEnd, "mcp", out var mcpStart, out var mcpEnd))
                {
                    return false;
                }

                var native = false;
                var objectStart = -1;
                var objectEnd = -1;
                if (TryFindDirectValue(source, mcpStart, mcpEnd, "servers", out var serversStart, out var serversEnd))
                {
                    // A malformed native container/entry must never fall back to a ready legacy entry.
                    if (source[serversStart] != '{') return false;
                    native = TryFindDirectValue(source, serversStart, serversEnd, "sgg-unity-mcp", out objectStart, out objectEnd);
                    if (native && source[objectStart] != '{') return false;
                }
                if (!native && !TryFindDirectObject(source, mcpStart, mcpEnd, "sgg-unity-mcp", out objectStart, out objectEnd)) return false;

                var server = source.Substring(objectStart, objectEnd - objectStart + 1);
                if (native)
                {
                    if (TryFindDirectValue(server, 0, server.Length - 1, "enabled", out _, out _)) return false;
                    if (!AreNativeOpenCodeOptionalFieldsValid(server, mode != ModeRemoteHttp)) return false;
                    if (TryFindDirectValue(server, 0, server.Length - 1, "disabled", out var disabledStart, out var disabledEnd)
                        && server.Substring(disabledStart, disabledEnd - disabledStart + 1) != "false") return false;
                    var executionTimeout = (double)OpenCodeMcpDefaultExecutionTimeoutMs;
                    if (!TryApplyLegacyOpenCodeDefault(source, rootStart, rootEnd, ref executionTimeout)
                        || !TryApplyOpenCodeTimeout(source, mcpStart, mcpEnd, ref executionTimeout)
                        || !TryApplyOpenCodeTimeout(server, 0, server.Length - 1, ref executionTimeout)
                        || executionTimeout < OpenCodeMcpCallTimeoutMs) return false;
                }
                else if (!ReadDirectBool(server, 0, server.Length - 1, "enabled", false)
                    || !IsSafeLegacyOpenCodeTimeout(ReadDirectDouble(server, 0, server.Length - 1, "timeout", 0)))
                {
                    return false;
                }

                if (!Uri.TryCreate(mcpUrl, UriKind.Absolute, out var endpoint)
                    || endpoint.Scheme != "http"
                    || !string.IsNullOrEmpty(endpoint.UserInfo)
                    || !string.IsNullOrEmpty(endpoint.Query)
                    || !string.IsNullOrEmpty(endpoint.Fragment)
                    || endpoint.AbsolutePath != "/mcp"
                    || mcpUrl.IndexOf('<') >= 0) return false;

                return mode == ModeRemoteHttp
                    ? string.Equals(ReadDirectString(server, 0, server.Length - 1, "type", string.Empty), "remote", StringComparison.Ordinal)
                        && string.Equals(ReadDirectString(server, 0, server.Length - 1, "url", string.Empty), mcpUrl, StringComparison.Ordinal)
                        && TryFindDirectValue(server, 0, server.Length - 1, "oauth", out var oauthStart, out var oauthEnd)
                        && server.Substring(oauthStart, oauthEnd - oauthStart + 1) == "false"
                        && TryFindDirectObject(server, 0, server.Length - 1, "headers", out var headersStart, out var headersEnd)
                        && string.Equals(ReadDirectString(server, headersStart, headersEnd, "X-SGG-Unity-MCP-Token", string.Empty), "{env:" + tokenEnv + "}", StringComparison.Ordinal)
                    : IsLoopbackHost(endpoint.Host.Trim('[', ']'))
                        && string.Equals(ReadDirectString(server, 0, server.Length - 1, "type", string.Empty), "local", StringComparison.Ordinal)
                        && TryFindDirectArray(server, 0, server.Length - 1, "command", out var commandStart, out var commandEnd)
                        && IsWindowsStdioCommand(server, commandStart, commandEnd, mcpUrl, tokenEnv);
            }
            catch
            {
                return false;
            }
        }

        static bool IsSafeLegacyOpenCodeTimeout(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value)
                && value >= OpenCodeMcpCallTimeoutMs && value <= 9007199254740991d && Math.Truncate(value) == value;
        }

        static bool TryApplyLegacyOpenCodeDefault(string source, int rootStart, int rootEnd, ref double executionTimeout)
        {
            if (!TryFindDirectObject(source, rootStart, rootEnd, "experimental", out var start, out var end)
                || !TryFindDirectValue(source, start, end, "mcp_timeout", out var valueStart, out var valueEnd)) return true;
            if (!double.TryParse(source.Substring(valueStart, valueEnd - valueStart + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || double.IsNaN(value) || double.IsInfinity(value) || value <= 0 || value > 9007199254740991d || Math.Truncate(value) != value) return false;
            executionTimeout = value;
            return true;
        }

        static bool AreNativeOpenCodeOptionalFieldsValid(string server, bool legacyShim)
        {
            if (TryFindDirectValue(server, 0, server.Length - 1, "protocol", out _, out _))
            {
                var protocol = ReadDirectString(server, 0, server.Length - 1, "protocol", string.Empty);
                if (protocol != "legacy" && protocol != "auto" && protocol != "2026-07-28") return false;
                if (legacyShim && protocol == "2026-07-28") return false;
            }
            if (TryFindDirectValue(server, 0, server.Length - 1, "codemode", out var boolStart, out var boolEnd))
            {
                var value = server.Substring(boolStart, boolEnd - boolStart + 1);
                if (value != "true" && value != "false") return false;
            }
            if (legacyShim && TryFindDirectValue(server, 0, server.Length - 1, "cwd", out var cwdStart, out _)
                && server[cwdStart] != '"') return false;
            return IsOptionalOpenCodeStringMapValid(server, legacyShim ? "environment" : "headers");
        }

        static bool IsOptionalOpenCodeStringMapValid(string server, string field)
        {
            if (!TryFindDirectValue(server, 0, server.Length - 1, field, out var start, out var end)) return true;
            if (server[start] != '{') return false;
            var cursor = start + 1;
            while (cursor < end)
            {
                SkipJsonWhitespace(server, ref cursor);
                if (cursor >= end) break;
                cursor = FindJsonStringEnd(server, cursor) + 1;
                SkipJsonWhitespace(server, ref cursor);
                cursor++;
                SkipJsonWhitespace(server, ref cursor);
                if (server[cursor] != '"') return false;
                cursor = FindJsonStringEnd(server, cursor) + 1;
                SkipJsonWhitespace(server, ref cursor);
                if (cursor < end) cursor++;
            }
            return true;
        }

        static bool TryApplyOpenCodeTimeout(string source, int parentStart, int parentEnd, ref double executionTimeout)
        {
            if (!TryFindDirectValue(source, parentStart, parentEnd, "timeout", out var timeoutStart, out var timeoutEnd)) return true;
            if (source[timeoutStart] != '{') return false;
            var cursor = timeoutStart + 1;
            while (cursor < timeoutEnd)
            {
                SkipJsonWhitespace(source, ref cursor);
                if (cursor >= timeoutEnd) break;
                var nameEnd = FindJsonStringEnd(source, cursor);
                var purpose = DecodeJsonString(source, cursor, nameEnd);
                if (purpose != "startup" && purpose != "catalog" && purpose != "execution") return false;
                cursor = nameEnd + 1;
                SkipJsonWhitespace(source, ref cursor);
                cursor++;
                SkipJsonWhitespace(source, ref cursor);
                var valueStart = cursor;
                var valueEnd = FindJsonValueEnd(source, valueStart, timeoutEnd);
                if (!double.TryParse(source.Substring(valueStart, valueEnd - valueStart + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    || double.IsNaN(value) || double.IsInfinity(value) || value <= 0 || value > 9007199254740991d || Math.Truncate(value) != value) return false;
                if (purpose == "execution") executionTimeout = value;
                cursor = valueEnd + 1;
                SkipJsonWhitespace(source, ref cursor);
                if (cursor < timeoutEnd) cursor++;
            }
            return true;
        }

        internal static bool IsLoopbackHost(string host)
        {
            return string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsBindSecurityReady(string host, bool allowNonLoopback, bool tokenPresent, string allowedOrigins)
        {
            return IsLoopbackHost(host) || (allowNonLoopback && tokenPresent && !string.IsNullOrEmpty(allowedOrigins));
        }

        static bool TryStripJsonComments(string source, out string result)
        {
            var builder = new StringBuilder(source == null ? 0 : source.Length);
            var inString = false;
            var escaped = false;
            for (var index = 0; source != null && index < source.Length; index++)
            {
                var current = source[index];
                if (inString)
                {
                    builder.Append(current);
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (current == '\\')
                    {
                        escaped = true;
                    }
                    else if (current == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (current == '"')
                {
                    inString = true;
                    builder.Append(current);
                    continue;
                }

                if (current == '/' && index + 1 < source.Length && source[index + 1] == '/')
                {
                    index += 2;
                    while (index < source.Length && source[index] != '\n') index++;
                    builder.Append('\n');
                    continue;
                }

                if (current == '/' && index + 1 < source.Length && source[index + 1] == '*')
                {
                    index += 2;
                    while (index + 1 < source.Length && (source[index] != '*' || source[index + 1] != '/')) index++;
                    if (index + 1 >= source.Length)
                    {
                        result = string.Empty;
                        return false;
                    }

                    index++;
                    builder.Append(' ');
                    continue;
                }

                builder.Append(current);
            }

            result = builder.ToString();
            return true;
        }

        static int FindJsonObjectEnd(string source, int objectStart)
        {
            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var index = objectStart; objectStart >= 0 && index < source.Length; index++)
            {
                var current = source[index];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (current == '\\') escaped = true;
                    else if (current == '"') inString = false;
                    continue;
                }

                if (current == '"') inString = true;
                else if (current == '{') depth++;
                else if (current == '}' && --depth == 0) return index;
            }

            return -1;
        }

        static bool TryGetSingleRootObject(string source, out int rootStart, out int rootEnd)
        {
            if (!IsValidJsoncDocument(source))
            {
                rootStart = -1;
                rootEnd = -1;
                return false;
            }

            rootStart = 0;
            while (rootStart < source.Length && char.IsWhiteSpace(source[rootStart])) rootStart++;
            rootEnd = rootStart < source.Length && source[rootStart] == '{' ? FindJsonObjectEnd(source, rootStart) : -1;
            if (rootEnd <= rootStart)
            {
                return false;
            }

            var cursor = rootEnd + 1;
            while (cursor < source.Length && char.IsWhiteSpace(source[cursor])) cursor++;
            return cursor == source.Length;
        }

        static bool IsValidJsoncDocument(string source)
        {
            var cursor = 0;
            SkipJsonWhitespace(source, ref cursor);
            if (!TryValidateJsonValue(source, ref cursor, 0)) return false;
            SkipJsonWhitespace(source, ref cursor);
            return cursor == source.Length;
        }

        static bool TryValidateJsonValue(string source, ref int cursor, int depth)
        {
            if (depth > 64) return false;
            SkipJsonWhitespace(source, ref cursor);
            if (cursor >= source.Length) return false;
            if (source[cursor] == '"')
            {
                var end = FindJsonStringEnd(source, cursor);
                if (end < 0 || !IsValidJsonString(source, cursor, end)) return false;
                cursor = end + 1;
                return true;
            }

            if (source[cursor] == '{') return TryValidateJsonObject(source, ref cursor, depth + 1);
            if (source[cursor] == '[') return TryValidateJsonArray(source, ref cursor, depth + 1);
            if (TryConsumeJsonLiteral(source, ref cursor, "true") || TryConsumeJsonLiteral(source, ref cursor, "false") || TryConsumeJsonLiteral(source, ref cursor, "null")) return true;
            return TryConsumeJsonNumber(source, ref cursor);
        }

        static bool TryValidateJsonObject(string source, ref int cursor, int depth)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            cursor++;
            SkipJsonWhitespace(source, ref cursor);
            if (cursor < source.Length && source[cursor] == '}')
            {
                cursor++;
                return true;
            }

            while (cursor < source.Length)
            {
                if (source[cursor] != '"') return false;
                var nameEnd = FindJsonStringEnd(source, cursor);
                if (nameEnd < 0 || !IsValidJsonString(source, cursor, nameEnd)) return false;
                if (!names.Add(DecodeJsonString(source, cursor, nameEnd))) return false;
                cursor = nameEnd + 1;
                SkipJsonWhitespace(source, ref cursor);
                if (cursor >= source.Length || source[cursor] != ':') return false;
                cursor++;
                if (!TryValidateJsonValue(source, ref cursor, depth)) return false;
                SkipJsonWhitespace(source, ref cursor);
                if (cursor >= source.Length) return false;
                if (source[cursor] == '}')
                {
                    cursor++;
                    return true;
                }

                if (source[cursor] != ',') return false;
                cursor++;
                SkipJsonWhitespace(source, ref cursor);
                if (cursor < source.Length && source[cursor] == '}')
                {
                    cursor++;
                    return true;
                }
            }

            return false;
        }

        static bool TryValidateJsonArray(string source, ref int cursor, int depth)
        {
            cursor++;
            SkipJsonWhitespace(source, ref cursor);
            if (cursor < source.Length && source[cursor] == ']')
            {
                cursor++;
                return true;
            }

            while (cursor < source.Length)
            {
                if (!TryValidateJsonValue(source, ref cursor, depth)) return false;
                SkipJsonWhitespace(source, ref cursor);
                if (cursor >= source.Length) return false;
                if (source[cursor] == ']')
                {
                    cursor++;
                    return true;
                }

                if (source[cursor] != ',') return false;
                cursor++;
                SkipJsonWhitespace(source, ref cursor);
                if (cursor < source.Length && source[cursor] == ']')
                {
                    cursor++;
                    return true;
                }
            }

            return false;
        }

        static bool TryConsumeJsonLiteral(string source, ref int cursor, string literal)
        {
            if (cursor + literal.Length > source.Length || !string.Equals(source.Substring(cursor, literal.Length), literal, StringComparison.Ordinal)) return false;
            cursor += literal.Length;
            return true;
        }

        static bool TryConsumeJsonNumber(string source, ref int cursor)
        {
            var start = cursor;
            if (cursor < source.Length && source[cursor] == '-') cursor++;
            if (cursor >= source.Length) return false;
            if (source[cursor] == '0')
            {
                cursor++;
                if (cursor < source.Length && char.IsDigit(source[cursor])) return false;
            }
            else
            {
                if (source[cursor] < '1' || source[cursor] > '9') return false;
                while (cursor < source.Length && char.IsDigit(source[cursor])) cursor++;
            }

            if (cursor < source.Length && source[cursor] == '.')
            {
                cursor++;
                var fractionStart = cursor;
                while (cursor < source.Length && char.IsDigit(source[cursor])) cursor++;
                if (cursor == fractionStart) return false;
            }

            if (cursor < source.Length && (source[cursor] == 'e' || source[cursor] == 'E'))
            {
                cursor++;
                if (cursor < source.Length && (source[cursor] == '+' || source[cursor] == '-')) cursor++;
                var exponentStart = cursor;
                while (cursor < source.Length && char.IsDigit(source[cursor])) cursor++;
                if (cursor == exponentStart) return false;
            }

            return cursor > start;
        }

        static void SkipJsonWhitespace(string source, ref int cursor)
        {
            while (cursor < source.Length && char.IsWhiteSpace(source[cursor])) cursor++;
        }

        static bool IsValidJsonString(string source, int start, int end)
        {
            for (var index = start + 1; index < end; index++)
            {
                var current = source[index];
                if (current < 0x20) return false;
                if (current != '\\') continue;
                index++;
                if (index >= end) return false;
                current = source[index];
                if (current == '"' || current == '\\' || current == '/' || current == 'b' || current == 'f' || current == 'n' || current == 'r' || current == 't') continue;
                if (current != 'u' || index + 4 >= end) return false;
                for (var offset = 1; offset <= 4; offset++)
                {
                    if (!Uri.IsHexDigit(source[index + offset])) return false;
                }

                index += 4;
            }

            return true;
        }

        static string DecodeJsonString(string source, int start, int end)
        {
            if (!IsValidJsonString(source, start, end)) return null;
            var builder = new StringBuilder(end - start - 1);
            for (var index = start + 1; index < end; index++)
            {
                var current = source[index];
                if (current != '\\')
                {
                    builder.Append(current);
                    continue;
                }

                current = source[++index];
                switch (current)
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
                        for (var offset = 1; offset <= 4; offset++) code = code * 16 + HexValue(source[index + offset]);
                        builder.Append((char)code);
                        index += 4;
                        break;
                }
            }

            return builder.ToString();
        }

        static int HexValue(char value)
        {
            if (value >= '0' && value <= '9') return value - '0';
            if (value >= 'a' && value <= 'f') return value - 'a' + 10;
            return value - 'A' + 10;
        }

        static bool TryFindDirectObject(string source, int parentStart, int parentEnd, string property, out int objectStart, out int objectEnd)
        {
            objectStart = -1;
            objectEnd = -1;
            if (!TryFindDirectValue(source, parentStart, parentEnd, property, out var valueStart, out var valueEnd)
                || source[valueStart] != '{')
            {
                return false;
            }

            objectStart = valueStart;
            objectEnd = valueEnd;
            return true;
        }

        static bool TryFindDirectArray(string source, int parentStart, int parentEnd, string property, out int arrayStart, out int arrayEnd)
        {
            arrayStart = -1;
            arrayEnd = -1;
            if (!TryFindDirectValue(source, parentStart, parentEnd, property, out var valueStart, out var valueEnd)
                || source[valueStart] != '[')
            {
                return false;
            }

            arrayStart = valueStart;
            arrayEnd = valueEnd;
            return true;
        }

        static bool TryFindDirectValue(string source, int parentStart, int parentEnd, string property, out int valueStart, out int valueEnd)
        {
            valueStart = -1;
            valueEnd = -1;
            if (string.IsNullOrEmpty(source) || parentStart < 0 || parentEnd <= parentStart || source[parentStart] != '{')
            {
                return false;
            }

            var found = false;
            var cursor = parentStart + 1;
            while (cursor < parentEnd)
            {
                while (cursor < parentEnd && char.IsWhiteSpace(source[cursor])) cursor++;
                if (cursor >= parentEnd || source[cursor] != '"') return false;
                var nameEnd = FindJsonStringEnd(source, cursor);
                if (nameEnd <= cursor) return false;
                var name = DecodeJsonString(source, cursor, nameEnd);
                if (name == null) return false;
                cursor = nameEnd + 1;
                while (cursor < parentEnd && char.IsWhiteSpace(source[cursor])) cursor++;
                if (cursor >= parentEnd || source[cursor] != ':') return false;
                cursor++;
                while (cursor < parentEnd && char.IsWhiteSpace(source[cursor])) cursor++;
                var currentEnd = FindJsonValueEnd(source, cursor, parentEnd);
                if (currentEnd < cursor) return false;
                if (string.Equals(name, property, StringComparison.Ordinal))
                {
                    if (found) return false;
                    found = true;
                    valueStart = cursor;
                    valueEnd = currentEnd;
                }

                cursor = currentEnd + 1;
                while (cursor < parentEnd && char.IsWhiteSpace(source[cursor])) cursor++;
                if (cursor >= parentEnd) break;
                if (source[cursor] != ',') return false;
                cursor++;
                while (cursor < parentEnd && char.IsWhiteSpace(source[cursor])) cursor++;
                if (cursor < parentEnd && source[cursor] == ',') return false;
            }

            return found;
        }

        static string ReadDirectString(string source, int parentStart, int parentEnd, string property, string fallback)
        {
            if (!TryFindDirectValue(source, parentStart, parentEnd, property, out var start, out var end)
                || source[start] != '"'
                || end <= start)
            {
                return fallback;
            }

            return DecodeJsonString(source, start, end) ?? fallback;
        }

        static int ReadDirectInt(string source, int parentStart, int parentEnd, string property, int fallback)
        {
            if (!TryFindDirectValue(source, parentStart, parentEnd, property, out var start, out var end))
            {
                return fallback;
            }

            return int.TryParse(source.Substring(start, end - start + 1), out var value) ? value : fallback;
        }

        static double ReadDirectDouble(string source, int parentStart, int parentEnd, string property, double fallback)
        {
            if (!TryFindDirectValue(source, parentStart, parentEnd, property, out var start, out var end))
            {
                return fallback;
            }

            return double.TryParse(source.Substring(start, end - start + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && !double.IsNaN(value) && !double.IsInfinity(value)
                ? value
                : fallback;
        }

        static bool ReadDirectBool(string source, int parentStart, int parentEnd, string property, bool fallback)
        {
            if (!TryFindDirectValue(source, parentStart, parentEnd, property, out var start, out var end))
            {
                return fallback;
            }

            var value = source.Substring(start, end - start + 1);
            return value == "true" ? true : value == "false" ? false : fallback;
        }

        static string ReadDirectArraySummary(string source, int parentStart, int parentEnd, string property)
        {
            if (!TryFindDirectArray(source, parentStart, parentEnd, property, out var start, out var end))
            {
                return string.Empty;
            }

            return Regex.Replace(source.Substring(start + 1, end - start - 1), "\\s+", " ").Trim();
        }

        static bool IsWindowsStdioCommand(string source, int arrayStart, int arrayEnd, string mcpUrl, string tokenEnv)
        {
            var values = new List<string>();
            var cursor = arrayStart + 1;
            while (cursor < arrayEnd)
            {
                while (cursor < arrayEnd && char.IsWhiteSpace(source[cursor])) cursor++;
                if (cursor >= arrayEnd || source[cursor] != '"') return false;
                var end = FindJsonStringEnd(source, cursor);
                if (end <= cursor || end > arrayEnd) return false;
                var value = DecodeJsonString(source, cursor, end);
                if (value == null) return false;
                values.Add(value);
                cursor = end + 1;
                while (cursor < arrayEnd && char.IsWhiteSpace(source[cursor])) cursor++;
                if (cursor >= arrayEnd) break;
                if (source[cursor] != ',') return false;
                cursor++;
            }

            return values.Count == 6
                && string.Equals(values[0], "powershell.exe", StringComparison.OrdinalIgnoreCase)
                && string.Equals(values[1], "-NoProfile", StringComparison.Ordinal)
                && string.Equals(values[2], "-ExecutionPolicy", StringComparison.Ordinal)
                && string.Equals(values[3], "Bypass", StringComparison.Ordinal)
                && string.Equals(values[4], "-Command", StringComparison.Ordinal)
                && string.Equals(values[5], WindowsStdioScript(mcpUrl, tokenEnv), StringComparison.Ordinal);
        }

        static int FindJsonValueEnd(string source, int valueStart, int parentEnd)
        {
            if (valueStart < 0 || valueStart >= parentEnd)
            {
                return -1;
            }

            if (source[valueStart] == '{') return FindJsonObjectEnd(source, valueStart);
            if (source[valueStart] == '[') return FindJsonArrayEnd(source, valueStart);
            if (source[valueStart] == '"') return FindJsonStringEnd(source, valueStart);

            var end = valueStart;
            while (end < parentEnd && source[end] != ',') end++;
            end--;
            while (end >= valueStart && char.IsWhiteSpace(source[end])) end--;
            return end;
        }

        static int FindJsonStringEnd(string source, int quoteStart)
        {
            var escaped = false;
            for (var index = quoteStart + 1; quoteStart >= 0 && index < source.Length; index++)
            {
                var current = source[index];
                if (escaped) escaped = false;
                else if (current == '\\') escaped = true;
                else if (current == '"') return index;
            }

            return -1;
        }

        static int FindJsonArrayEnd(string source, int arrayStart)
        {
            var arrayDepth = 0;
            var objectDepth = 0;
            var inString = false;
            var escaped = false;
            for (var index = arrayStart; arrayStart >= 0 && index < source.Length; index++)
            {
                var current = source[index];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (current == '\\') escaped = true;
                    else if (current == '"') inString = false;
                    continue;
                }

                if (current == '"') inString = true;
                else if (current == '[') arrayDepth++;
                else if (current == ']')
                {
                    arrayDepth--;
                    if (arrayDepth == 0 && objectDepth == 0) return index;
                }
                else if (current == '{') objectDepth++;
                else if (current == '}') objectDepth--;
            }

            return -1;
        }

        public static NodeRuntimeInstaller.InstallResult ApplyLoopbackMode()
        {
            var ensureResult = GatewayPolicyManager.EnsureUserConfig();
            if (!ensureResult.Success)
            {
                return ensureResult;
            }

            try
            {
                var configPath = UnityMcpPackagePaths.UserGatewayConfigPath;
                var source = File.ReadAllText(configPath);
                source = UpsertMcpString(source, "host", "127.0.0.1");
                source = RemoveMcpValue(source, "token_env");
                source = UpsertMcpBool(source, "allow_non_loopback", false);
                source = UpsertMcpArray(source, "allowed_origins", new string[0]);
                File.WriteAllText(configPath, source);
                return NodeRuntimeInstaller.InstallResult.Ok("Applied loopback/stdout shim mode and disabled Gateway token auth for Windows loopback. Restart Gateway to reload network settings.");
            }
            catch (Exception exception)
            {
                return NodeRuntimeInstaller.InstallResult.Fail("Could not apply loopback mode: " + exception.Message);
            }
        }

        public static NodeRuntimeInstaller.InstallResult ApplyRemoteHttpMode()
        {
            var ensureResult = GatewayPolicyManager.EnsureUserConfig();
            if (!ensureResult.Success)
            {
                return ensureResult;
            }

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(McpTokenEnvName)))
            {
                return NodeRuntimeInstaller.InstallResult.Fail("Remote HTTP mode requires " + McpTokenEnvName + " in this Unity Editor process. Generate or paste a token, then click Use Token For This Session first.");
            }

            try
            {
                var configPath = UnityMcpPackagePaths.UserGatewayConfigPath;
                var source = File.ReadAllText(configPath);
                source = UpsertMcpString(source, "host", "0.0.0.0");
                source = UpsertMcpString(source, "token_env", McpTokenEnvName);
                source = UpsertMcpBool(source, "allow_non_loopback", true);
                source = UpsertMcpArray(source, "allowed_origins", new[] { "http://localhost", "http://127.0.0.1" });
                File.WriteAllText(configPath, source);
                return NodeRuntimeInstaller.InstallResult.Ok("Applied Remote HTTP mode. Restart Gateway and ensure Windows Firewall allows trusted WSL/local access only.");
            }
            catch (Exception exception)
            {
                return NodeRuntimeInstaller.InstallResult.Fail("Could not apply Remote HTTP mode: " + exception.Message);
            }
        }

        public static NodeRuntimeInstaller.InstallResult PreviewOpenCodeConfig(string selectedMode)
        {
            return RunOpenCodeSetup(selectedMode, false);
        }

        internal static NodeRuntimeInstaller.InstallResult WriteOpenCodeConfig(
            string selectedMode,
            string expectedConfigPath,
            string confirmedFallbackConfigPath = null)
        {
            return RunOpenCodeSetup(selectedMode, true, expectedConfigPath, confirmedFallbackConfigPath);
        }

        public static NodeRuntimeInstaller.InstallResult OpenOpenCodeConfig()
        {
            OpenCodeConfigPathResolver.Result target;
            string error;
            if (!TryResolveOpenCodeTarget(out target, out error))
            {
                return NodeRuntimeInstaller.InstallResult.Fail("OpenCode config target is unavailable: " + error);
            }

            if (target.HasConflict)
            {
                if (target.HasHiddenLayer) return NodeRuntimeInstaller.InstallResult.Fail(HiddenLayerConflictMessage(target));
                return NodeRuntimeInstaller.InstallResult.Fail("Both OpenCode config files exist. Resolve the conflict manually before opening a setup target: " + target.JsoncPath + " | " + target.JsonPath);
            }

            if (!File.Exists(target.ConfigPath))
            {
                return NodeRuntimeInstaller.InstallResult.Fail("OpenCode config does not exist yet: " + target.ConfigPath);
            }

            EditorUtility.RevealInFinder(target.ConfigPath);
            return NodeRuntimeInstaller.InstallResult.Ok("Opened OpenCode config at " + target.ConfigPath);
        }

        public static string OpenCodeConfigSnippet(string selectedMode)
        {
            return OpenCodeConfigSnippet(GetStatus(selectedMode));
        }

        internal static string OpenCodeConfigSnippet(NetworkStatus status)
        {
            return status.SelectedMode == ModeRemoteHttp ? RemoteHttpSnippet(status) : WindowsStdioSnippet(status);
        }

        public static string FirewallRuleCommand()
        {
            return "New-NetFirewallRule -DisplayName \"SGG Unity MCP Gateway WSL\" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 27182 -Profile Private";
        }

        public static string NormalizeMode(string labelOrMode)
        {
            return labelOrMode == ModeRemoteHttp || labelOrMode == ModeRemoteHttpLabel ? ModeRemoteHttp : ModeWindowsStdio;
        }

        public static string LabelForMode(string mode)
        {
            return NormalizeMode(mode) == ModeRemoteHttp ? ModeRemoteHttpLabel : ModeWindowsStdioLabel;
        }

        static NodeRuntimeInstaller.InstallResult RunOpenCodeSetup(
            string selectedMode,
            bool write,
            string expectedConfigPath = null,
            string confirmedFallbackConfigPath = null)
        {
            OpenCodeConfigPathResolver.Result target;
            string targetError;
            if (!TryResolveOpenCodeTarget(out target, out targetError))
            {
                return NodeRuntimeInstaller.InstallResult.Fail("OpenCode config target is unavailable: " + targetError);
            }

            var writeTargetError = ValidateOpenCodeSetupTarget(target, write, expectedConfigPath, confirmedFallbackConfigPath);
            if (!string.IsNullOrEmpty(writeTargetError))
            {
                return NodeRuntimeInstaller.InstallResult.Fail(writeTargetError);
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

            var status = GetStatus(selectedMode);
            var setupConfigPath = File.Exists(gatewayStatus.ConfigPath) ? gatewayStatus.ConfigPath : gatewayStatus.ExampleConfigPath;
            OpenCodeConfigPathResolver.Result currentTarget;
            string currentTargetError;
            if (!TryResolveOpenCodeTarget(out currentTarget, out currentTargetError))
            {
                return NodeRuntimeInstaller.InstallResult.Fail("OpenCode config target became unavailable before setup: " + currentTargetError);
            }

            writeTargetError = ValidateOpenCodeSetupTarget(currentTarget, write, expectedConfigPath, confirmedFallbackConfigPath);
            if (!string.IsNullOrEmpty(writeTargetError))
            {
                return NodeRuntimeInstaller.InstallResult.Fail(writeTargetError);
            }

            target = currentTarget;
            var arguments = ProcessUtil.Quote(gatewayStatus.BundledCliPath)
                + " setup opencode"
                + " --config " + ProcessUtil.Quote(setupConfigPath)
                + " --output " + ProcessUtil.Quote(target.ConfigPath)
                + " --connection-mode " + NormalizeMode(selectedMode)
                + " --url " + ProcessUtil.Quote(status.McpUrl)
                + (write ? " --write" : " --dry-run --json");
            var pathPrepend = nodeStatus.IsPortable ? Path.GetDirectoryName(nodeStatus.NodePath) : null;
            var ok = ProcessUtil.TryRun(nodeStatus.NodePath, arguments, gatewayStatus.RuntimeRoot, out var stdout, out var stderr, 30000, pathPrepend);
            if (!ok)
            {
                return NodeRuntimeInstaller.InstallResult.Fail("OpenCode setup failed: " + TrimForDialog(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr));
            }

            if (!write)
            {
                GUIUtilityBridge.CopyText(stdout.Trim());
                return NodeRuntimeInstaller.InstallResult.Ok("Copied OpenCode config dry-run preview to clipboard. Target: " + target.ConfigPath);
            }

            return NodeRuntimeInstaller.InstallResult.Ok("Created/updated the selected OpenCode config without overwriting unrelated settings: " + target.ConfigPath + ". Ancestor/global/inline effective config is not verified; run the MCP smoke test in the client.");
        }

        internal static string ValidateOpenCodeWriteTarget(
            OpenCodeConfigPathResolver.Result target,
            string expectedConfigPath,
            string confirmedFallbackConfigPath)
        {
            if (target == null)
            {
                return "OpenCode config target is unavailable.";
            }

            if (!string.Equals(target.ConfigPath, expectedConfigPath, StringComparison.Ordinal))
            {
                return "OpenCode config target changed after it was displayed. Refresh and confirm the current target before writing: " + target.ConfigPath;
            }

            if (target.RequiresConfirmation
                && !string.Equals(target.ConfigPath, confirmedFallbackConfigPath, StringComparison.Ordinal))
            {
                return "OpenCode workspace fallback requires confirmation before writing: " + target.ConfigPath;
            }

            return null;
        }

        internal static string ValidateOpenCodeSetupTarget(
            OpenCodeConfigPathResolver.Result target,
            bool write,
            string expectedConfigPath,
            string confirmedFallbackConfigPath)
        {
            if (target == null)
            {
                return "OpenCode config target is unavailable.";
            }
            if (target.HasConflict)
            {
                if (target.HasHiddenLayer) return HiddenLayerConflictMessage(target);
                return "Both OpenCode config files exist. Setup will not choose, merge, rename or delete either file: " + target.JsoncPath + " | " + target.JsonPath;
            }
            return write ? ValidateOpenCodeWriteTarget(target, expectedConfigPath, confirmedFallbackConfigPath) : null;
        }

        static string WindowsStdioSnippet(NetworkStatus status)
        {
            var script = EscapeJson(WindowsStdioScript(status.McpUrl, status.TokenEnv));
            return "{\n"
                + "  \"mcp\": {\n"
                + "    \"servers\": {\n"
                + "      \"sgg-unity-mcp\": {\n"
                + "        \"type\": \"local\",\n"
                + "        \"command\": [\n"
                + "          \"powershell.exe\",\n"
                + "          \"-NoProfile\",\n"
                + "          \"-ExecutionPolicy\",\n"
                + "          \"Bypass\",\n"
                + "          \"-Command\",\n"
                + "          \"" + script + "\"\n"
                + "        ],\n"
                + "        \"disabled\": false,\n"
                + "        \"protocol\": \"legacy\",\n"
                + OpenCodeTimeoutSnippet()
                + "      }\n"
                + "    }\n"
                + "  }\n"
                + "}";
        }

        static string RemoteHttpSnippet(NetworkStatus status)
        {
            return "{\n"
                + "  \"mcp\": {\n"
                + "    \"servers\": {\n"
                + "      \"sgg-unity-mcp\": {\n"
                + "        \"type\": \"remote\",\n"
                + "        \"url\": \"" + EscapeJson(status.McpUrl) + "\",\n"
                + "        \"headers\": { \"X-SGG-Unity-MCP-Token\": \"" + EscapeJson("{env:" + status.TokenEnv + "}") + "\" },\n"
                + "        \"oauth\": false,\n"
                + "        \"disabled\": false,\n"
                + OpenCodeTimeoutSnippet()
                + "      }\n"
                + "    }\n"
                + "  }\n"
                + "}";
        }

        static string OpenCodeTimeoutSnippet()
        {
            return "        \"timeout\": { \"startup\": " + OpenCodeMcpStartupTimeoutMs
                + ", \"catalog\": " + OpenCodeMcpCatalogTimeoutMs
                + ", \"execution\": " + OpenCodeMcpCallTimeoutMs + " }\n";
        }

        static string PowerShellSingleQuoted(string value)
        {
            return "'" + (value ?? string.Empty).Replace("'", "''") + "'";
        }

        static string WindowsStdioScript(string mcpUrl, string tokenEnv)
        {
            return "$root=Join-Path $env:LOCALAPPDATA 'SGG\\UnityMcpGateway'; $pointer=Join-Path $root 'gateway\\active-runtime.json'; $runtime=(Get-Content -Raw $pointer | ConvertFrom-Json).runtime_root; $node=Join-Path $root 'runtime\\node-v22.16.0-win-x64\\node.exe'; $cli=Join-Path $runtime 'gateway-cli.mjs'; & $node $cli stdio-shim --url "
                + PowerShellSingleQuoted(mcpUrl) + " --token-env " + PowerShellSingleQuoted(tokenEnv) + " --legacy-text-result";
        }

        static string DetectWindowsHostIp()
        {
            if (ProcessUtil.TryRun("powershell.exe", "-NoProfile -Command \"$ip=(Get-NetIPAddress -AddressFamily IPv4 -InterfaceAlias 'vEthernet (WSL*)' -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty IPAddress); if ($ip) { $ip }\"", null, out var stdout, out _, 5000))
            {
                return stdout.Trim();
            }

            return string.Empty;
        }

        static string MessageFor(string host, bool allowNonLoopback, bool tokenPresent, string allowedOrigins)
        {
            if (host == "127.0.0.1" || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return "Loopback-only Gateway. Use Windows stdio shim for WSL same-machine access.";
            }

            return allowNonLoopback && tokenPresent && !string.IsNullOrEmpty(allowedOrigins)
                ? "Remote HTTP prerequisites are configured. Restrict access with Windows Firewall."
                : "Remote HTTP is not safe/ready: token, allow_non_loopback and allowed_origins are required.";
        }

        static string UpsertMcpString(string source, string property, string value)
        {
            return UpsertMcpValue(source, property, "\"" + EscapeJson(value) + "\"");
        }

        static string UpsertMcpBool(string source, string property, bool value)
        {
            return UpsertMcpValue(source, property, value ? "true" : "false");
        }

        static string UpsertMcpArray(string source, string property, string[] values)
        {
            var builder = new StringBuilder();
            builder.Append('[');
            for (var index = 0; index < values.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append(", ");
                }

                builder.Append('"').Append(EscapeJson(values[index])).Append('"');
            }

            builder.Append(']');
            return UpsertMcpValue(source, property, builder.ToString());
        }

        static string UpsertMcpValue(string source, string property, string jsonValue)
        {
            var pattern = "\\\"" + property + "\\\"\\s*:\\s*(\\\"[^\\\"]*\\\"|true|false|[0-9]+|\\[[^\\]]*\\])";
            var replacement = "\"" + property + "\": " + jsonValue;
            if (Regex.IsMatch(source, pattern, RegexOptions.Singleline, TimeSpan.FromMilliseconds(250)))
            {
                return Regex.Replace(source, pattern, replacement, RegexOptions.Singleline, TimeSpan.FromMilliseconds(250));
            }

            var mcpMatch = Regex.Match(source, "\\\"mcp\\\"\\s*:\\s*\\{", RegexOptions.None, TimeSpan.FromMilliseconds(250));
            if (!mcpMatch.Success)
            {
                return source;
            }

            var insertAt = mcpMatch.Index + mcpMatch.Length;
            return source.Insert(insertAt, "\n    \"" + property + "\": " + jsonValue + ",");
        }

        static string RemoveMcpValue(string source, string property)
        {
            var valuePattern = "(\\\"[^\\\"]*\\\"|true|false|[0-9]+|\\[[^\\]]*\\])";
            var trailingCommaPattern = "\\s*\\\"" + property + "\\\"\\s*:\\s*" + valuePattern + "\\s*,";
            var trailingComma = new Regex(trailingCommaPattern, RegexOptions.Singleline, TimeSpan.FromMilliseconds(250));
            if (trailingComma.IsMatch(source))
            {
                return trailingComma.Replace(source, string.Empty, 1);
            }

            var leadingCommaPattern = ",\\s*\\\"" + property + "\\\"\\s*:\\s*" + valuePattern;
            var leadingComma = new Regex(leadingCommaPattern, RegexOptions.Singleline, TimeSpan.FromMilliseconds(250));
            return leadingComma.Replace(source, string.Empty, 1);
        }

        static string EscapeJson(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        static string TrimForDialog(string value)
        {
            var text = string.IsNullOrWhiteSpace(value) ? "No output" : value.Trim();
            const int maxLength = 1800;
            return text.Length <= maxLength ? text : text.Substring(0, maxLength) + "\n...";
        }

        public sealed class NetworkStatus
        {
            public string SelectedMode = ModeWindowsStdio;
            public string ConfigPath = string.Empty;
            public string OpenCodeWorkspaceRoot = string.Empty;
            public string OpenCodeWorkspaceHash = string.Empty;
            public string OpenCodePath = string.Empty;
            public string OpenCodeJsoncPath = string.Empty;
            public string OpenCodeJsonPath = string.Empty;
            public string OpenCodeTargetSource = string.Empty;
            public string OpenCodeTargetState = string.Empty;
            public bool OpenCodeTargetAvailable;
            public bool OpenCodeConfigConflict;
            public bool OpenCodeRequiresConfirmation;
            public bool OpenCodeNestedProject;
            public bool OpenCodeSavedWorkspaceRejected;
            public string OpenCodeTargetMessage = string.Empty;
            public string Host = "127.0.0.1";
            public int Port = 27182;
            public bool AllowNonLoopback;
            public string AllowedOrigins = string.Empty;
            public string TokenEnv = McpTokenEnvName;
            public bool TokenPresent;
            public bool LoopbackBind;
            public bool BindSecurityReady;
            public string WindowsHostIp = string.Empty;
            public string McpUrl = string.Empty;
            public bool OpenCodeConfigReady;
            public string Message = string.Empty;
        }
    }
}
