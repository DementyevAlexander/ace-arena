using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Commands;
using SGG.UnityMCP.Editor.Paths;
using SGG.UnityMCP.Editor.Setup;
using SGG.UnityMCP.Editor.UI.Localization;
using SGG.UnityMCP.Editor.UI.State;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace SGG.UnityMCP.Editor.UI
{
    public sealed partial class UnityMcpSetupWindow
    {
        UnityEditor.PackageManager.Requests.AddRequest m_canonicalPathInstallRequest;
        Label m_checklistUnityBridge;
        Label m_checklistPortableNode;
        Label m_checklistGatewayRuntime;
        Label m_checklistGatewayProcess;
        Label m_checklistCanonicalPathRegistry;
        Label m_checklistMcpToken;
        Label m_checklistMcpClientConfig;
        Label m_checklistPermissionsPolicy;
        Label m_checklistCatalogPolicySummary;
        int m_lastCatalogPolicySummaryCommandCount = -1;
        int m_lastGatewayVirtualCommandCount;
        PolicyPreviewContext m_lastCatalogPolicyContext;
        Label m_checklistRoslynCompiler;
        Label m_checklistUnityAiAssistant;
        string m_lastGatewayRuntimeError = string.Empty;
        Label m_nodeStatus;
        Label m_nodeVersion;
        Label m_nodePath;
        Label m_npmPath;
        VisualElement m_nodeIndicator;
        Button m_installPortableNodeButton;
        Label m_gatewayStatus;
        Label m_gatewayPath;
        Label m_gatewayEntry;
        VisualElement m_gatewayIndicator;
        Button m_installRuntimeButton;
        Button m_startGatewayButton;
        Button m_stopGatewayButton;
        Label m_processStatus;
        Label m_processHealth;
        Label m_processBody;
        Label m_processLogPath;
        Label m_processLogTail;
        VisualElement m_processIndicator;
        Button m_setupRefreshHealthButton;
        Button m_statusRefreshHealthButton;
        Label m_setupMcpTokenEnv;
        Label m_setupMcpTokenStatus;
        Label m_setupMcpClientHeader;
        TextField m_setupMcpSessionToken;
        PopupField<string> m_mcpConnectionModeField;
        Label m_mcpConnectionSelectedMode;
        Label m_mcpConnectionGatewayBind;
        Label m_mcpConnectionMcpUrl;
        Label m_mcpConnectionWslUrl;
        Label m_mcpConnectionTokenStatus;
        Label m_mcpConnectionSecurityStatus;
        Label m_mcpConnectionConfigPath;
        TextField m_mcpConnectionOpenCodeWorkspaceRoot;
        GatewayNetworkModeManager.NetworkStatus m_openCodePresentationNetworkStatus;
        Label m_mcpConnectionOpenCodePath;
        Label m_mcpConnectionOpenCodeStatus;
        Label m_mcpConnectionReportedWorkspace;
        Label m_setupMcpUrl;
        Label m_setupHealthUrl;
        Label m_setupProjectId;
        Label m_setupCanonicalPathRegistry;
        Label m_setupCanonicalPathDependency;
        Label m_setupCanonicalPathManifest;
        Button m_addCanonicalPathRegistryButton;
        Button m_installCanonicalPathButton;
        Label m_setupRoslynStatus;
        Label m_setupRoslynPath;
        Label m_setupRoslynFeature;
        Label m_setupRoslynPolicyRisk;
        Label m_setupUnityAiSummary;
        Label m_statusProcessStatus;
        Label m_statusProcessHealth;
        Label m_statusProcessBody;
        Label m_statusProcessLogPath;
        Label m_statusProcessLogTail;
        VisualElement m_statusProcessIndicator;
        Label m_statusConnectedProject;
        Label m_statusCommandsHash;
        Label m_statusFeatureFlags;
        Label m_statusPolicyProfile;
        Label m_statusHeartbeatAge;
        Label m_statusGatewayRuntimeVersion;
        Label m_statusUnityPackageVersion;
        Label m_statusCanonicalPathVersion;
        Label m_statusBridgeToken;
        Label m_statusMcpToken;
        Label m_statusRuntimePolicy;
        Label m_bridgeStatus;
        Label m_bridgeProject;
        Label m_bridgePort;
        Label m_heartbeatPath;
        Label m_heartbeatLastWrite;
        VisualElement m_bridgeIndicator;
        PopupField<string> m_languageField;
        Label m_setupLanguageCurrent;
        NodeRuntimeInstaller.NodeRuntimeStatus m_lastNodeRuntimeStatus;

        void OpenRoslynCommandsFilter()
        {
            SelectTab("Commands");
            if (m_commandsGroupFilter != null)
            {
                m_commandsGroupFilter.value = "editor";
            }

            if (m_commandsRiskFilter != null)
            {
                m_commandsRiskFilter.value = "execute";
            }

            RefreshCommands();
            m_lastActionLabel.text = "Commands filter opened for editor / execute-risk commands.";
        }

        string CanonicalPathManifestSnippet()
        {
            var builder = new StringBuilder();
            builder.Append("{\n");
            builder.Append("  \"scopedRegistries\": [\n");
            builder.Append(CanonicalPathRegistryObjectBlock("    ")).Append("\n");
            builder.Append("  ],\n");
            builder.Append("  \"dependencies\": {\n");
            builder.Append("    \"com.sungeargames.unity-mcp\": \"file:../path/to/com.sungeargames.unity-mcp\",\n");
            builder.Append("    ").Append(JsonUtil.String(CanonicalPathPackageId)).Append(": ").Append(JsonUtil.String(CanonicalPathVersion)).Append("\n");
            builder.Append("  }\n");
            builder.Append("}\n");
            return builder.ToString();
        }

        bool TryEnsureCanonicalPathScopedRegistry(string source, out string updatedSource, out bool changed, out string detail)
        {
            updatedSource = source;
            changed = false;
            detail = string.Empty;

            var scopedRegistriesIndex = FindJsonProperty(source, "scopedRegistries", 0, source.Length);
            if (scopedRegistriesIndex < 0)
            {
                return TryInsertScopedRegistriesProperty(source, out updatedSource, out changed, out detail);
            }

            var colonIndex = source.IndexOf(':', scopedRegistriesIndex);
            if (colonIndex < 0)
            {
                detail = "Packages/manifest.json scopedRegistries property is malformed.";
                return false;
            }

            var arrayStart = IndexOfNextNonWhitespace(source, colonIndex + 1);
            if (arrayStart < 0 || arrayStart >= source.Length || source[arrayStart] != '[')
            {
                detail = "Packages/manifest.json has scopedRegistries, but it is not a JSON array.";
                return false;
            }

            var arrayEnd = FindMatchingBracket(source, arrayStart, '[', ']');
            if (arrayEnd < 0)
            {
                detail = "Packages/manifest.json scopedRegistries array is malformed.";
                return false;
            }

            if (TryFindRegistryObjectWithUrl(source, arrayStart, arrayEnd, out var objectStart, out var objectEnd))
            {
                if (RegistryObjectContainsScope(source, objectStart, objectEnd))
                {
                    detail = CanonicalPathScope + " already points at " + CanonicalPathRegistryUrl + ".";
                    return true;
                }

                updatedSource = AddScopeToRegistryObject(source, objectStart, objectEnd);
                changed = !string.Equals(source, updatedSource, StringComparison.Ordinal);
                detail = "Added " + CanonicalPathScope + " to the existing " + CanonicalPathRegistryUrl + " scoped registry.";
                return true;
            }

            updatedSource = InsertRegistryObjectIntoArray(source, arrayStart, arrayEnd);
            changed = true;
            detail = "Added " + CanonicalPathScope + " scoped registry for " + CanonicalPathRegistryUrl + ".";
            return true;
        }

        bool TryInsertScopedRegistriesProperty(string source, out string updatedSource, out bool changed, out string detail)
        {
            updatedSource = source;
            changed = false;
            detail = string.Empty;
            var objectStart = IndexOfNextNonWhitespace(source, 0);
            if (objectStart < 0 || objectStart >= source.Length || source[objectStart] != '{')
            {
                detail = "Packages/manifest.json root must be a JSON object.";
                return false;
            }

            var objectEnd = FindMatchingBracket(source, objectStart, '{', '}');
            if (objectEnd < 0)
            {
                detail = "Packages/manifest.json root object is malformed.";
                return false;
            }

            var hasExistingProperties = source.Substring(objectStart + 1, objectEnd - objectStart - 1).Trim().Length > 0;
            var insertion = "\n  \"scopedRegistries\": [\n"
                + CanonicalPathRegistryObjectBlock("    ")
                + "\n  ]"
                + (hasExistingProperties ? "," : string.Empty)
                + "\n";
            updatedSource = source.Insert(objectStart + 1, insertion);
            changed = true;
            detail = "Added scopedRegistries with " + CanonicalPathScope + " -> " + CanonicalPathRegistryUrl + ".";
            return true;
        }

        string CanonicalPathRegistryObjectBlock(string objectIndent)
        {
            var memberIndent = objectIndent + "  ";
            return objectIndent + "{\n"
                + memberIndent + "\"name\": " + JsonUtil.String(CanonicalPathRegistryName) + ",\n"
                + memberIndent + "\"url\": " + JsonUtil.String(CanonicalPathRegistryUrl) + ",\n"
                + memberIndent + "\"scopes\": [" + JsonUtil.String(CanonicalPathScope) + "]\n"
                + objectIndent + "}";
        }

        string InsertRegistryObjectIntoArray(string source, int arrayStart, int arrayEnd)
        {
            var body = source.Substring(arrayStart + 1, arrayEnd - arrayStart - 1);
            var hasExistingObjects = body.Trim().Length > 0;
            var insertion = "\n"
                + CanonicalPathRegistryObjectBlock("    ")
                + (hasExistingObjects ? "," : string.Empty)
                + "\n  ";
            return source.Insert(arrayStart + 1, insertion);
        }

        string AddScopeToRegistryObject(string source, int objectStart, int objectEnd)
        {
            if (TryFindJsonArrayProperty(source, objectStart, objectEnd, "scopes", out var arrayStart, out var arrayEnd))
            {
                var body = source.Substring(arrayStart + 1, arrayEnd - arrayStart - 1);
                var insertion = body.Trim().Length == 0
                    ? JsonUtil.String(CanonicalPathScope)
                    : body.Contains("\n") ? ",\n        " + JsonUtil.String(CanonicalPathScope) : ", " + JsonUtil.String(CanonicalPathScope);
                return source.Insert(arrayEnd, insertion);
            }

            return source.Insert(objectEnd, ",\n      \"scopes\": [" + JsonUtil.String(CanonicalPathScope) + "]");
        }

        bool TryFindRegistryObjectWithUrl(string source, int arrayStart, int arrayEnd, out int objectStart, out int objectEnd)
        {
            objectStart = -1;
            objectEnd = -1;
            var index = arrayStart + 1;
            while (index < arrayEnd)
            {
                index = IndexOfNextNonWhitespace(source, index);
                if (index < 0 || index >= arrayEnd)
                {
                    return false;
                }

                if (source[index] == ',')
                {
                    index++;
                    continue;
                }

                if (source[index] != '{')
                {
                    index++;
                    continue;
                }

                var candidateEnd = FindMatchingBracket(source, index, '{', '}');
                if (candidateEnd < 0 || candidateEnd > arrayEnd)
                {
                    return false;
                }

                var objectText = source.Substring(index, candidateEnd - index + 1);
                if (RegistryUrlsMatch(JsonUtil.ExtractString(objectText, "url"), CanonicalPathRegistryUrl))
                {
                    objectStart = index;
                    objectEnd = candidateEnd;
                    return true;
                }

                index = candidateEnd + 1;
            }

            return false;
        }

        bool TryFindCanonicalPathRegistryObject(string source, out int objectStart, out int objectEnd)
        {
            objectStart = -1;
            objectEnd = -1;
            var scopedRegistriesIndex = FindJsonProperty(source, "scopedRegistries", 0, source.Length);
            if (scopedRegistriesIndex < 0)
            {
                return false;
            }

            var colonIndex = source.IndexOf(':', scopedRegistriesIndex);
            if (colonIndex < 0)
            {
                return false;
            }

            var arrayStart = IndexOfNextNonWhitespace(source, colonIndex + 1);
            if (arrayStart < 0 || arrayStart >= source.Length || source[arrayStart] != '[')
            {
                return false;
            }

            var arrayEnd = FindMatchingBracket(source, arrayStart, '[', ']');
            return arrayEnd >= 0 && TryFindRegistryObjectWithUrl(source, arrayStart, arrayEnd, out objectStart, out objectEnd);
        }

        bool RegistryUrlsMatch(string actual, string expected)
        {
            return string.Equals(NormalizeRegistryUrl(actual), NormalizeRegistryUrl(expected), StringComparison.OrdinalIgnoreCase);
        }

        string NormalizeRegistryUrl(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().TrimEnd('/');
        }

        bool RegistryObjectContainsScope(string source, int objectStart, int objectEnd)
        {
            return source.Substring(objectStart, objectEnd - objectStart + 1).Contains(JsonUtil.String(CanonicalPathScope));
        }

        bool TryFindJsonArrayProperty(string source, int startIndex, int endIndex, string propertyName, out int arrayStart, out int arrayEnd)
        {
            arrayStart = -1;
            arrayEnd = -1;
            var propertyIndex = FindJsonProperty(source, propertyName, startIndex, endIndex);
            if (propertyIndex < 0)
            {
                return false;
            }

            var colonIndex = source.IndexOf(':', propertyIndex);
            if (colonIndex < 0 || colonIndex > endIndex)
            {
                return false;
            }

            arrayStart = IndexOfNextNonWhitespace(source, colonIndex + 1);
            if (arrayStart < 0 || arrayStart > endIndex || source[arrayStart] != '[')
            {
                return false;
            }

            arrayEnd = FindMatchingBracket(source, arrayStart, '[', ']');
            return arrayEnd >= 0 && arrayEnd <= endIndex;
        }

        int FindJsonProperty(string source, string propertyName, int startIndex, int endIndex)
        {
            var marker = JsonUtil.String(propertyName);
            var index = source.IndexOf(marker, startIndex, StringComparison.Ordinal);
            return index >= 0 && index < endIndex ? index : -1;
        }

        int IndexOfNextNonWhitespace(string source, int startIndex)
        {
            for (var index = Math.Max(0, startIndex); index < source.Length; index++)
            {
                if (!char.IsWhiteSpace(source[index]))
                {
                    return index;
                }
            }

            return -1;
        }

        int FindMatchingBracket(string source, int startIndex, char open, char close)
        {
            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var index = startIndex; index < source.Length; index++)
            {
                var character = source[index];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (character == '"')
                {
                    inString = true;
                    continue;
                }

                if (character == open)
                {
                    depth++;
                    continue;
                }

                if (character == close)
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

        void BuildSetupPanel()
        {
            HelpRow(m_setupPanel, "Setup installs and starts the local Gateway pieces. Install/Start/Stop and policy shortcuts can change local config or processes; Refresh and Copy actions are safe read-only checks.");

            var checklistSection = AddSection(m_setupPanel, "Setup Checklist");
            HelpRow(checklistSection, "Required items connect Unity to the local Gateway. Recommended items help clients connect and make permissions explicit. Optional items expand tooling but are not required for the baseline.");
            m_checklistUnityBridge = AddChecklistRow(checklistSection, "Unity bridge", "required", "Refresh Status; keep this Unity project open.");
            m_checklistUnityBridge.name = "setup-checklist-unity-bridge";
            m_checklistPortableNode = AddChecklistRow(checklistSection, "Portable Node", "required", "Install Portable Node.");
            m_checklistGatewayRuntime = AddChecklistRow(checklistSection, "Gateway Runtime", "required", "Install Runtime.");
            m_checklistGatewayProcess = AddChecklistRow(checklistSection, "Gateway Process", "required", "Start Gateway, then Refresh Health.");
            m_checklistCanonicalPathRegistry = AddChecklistRow(checklistSection, "CanonicalPath", "required", "Add Scoped Registry, then Install CanonicalPath.");
            m_checklistMcpToken = AddChecklistRow(checklistSection, "MCP Token", "recommended", "Generate or paste a token for this Unity Editor session; localhost can run without it.");
            m_checklistMcpClientConfig = AddChecklistRow(checklistSection, "MCP Client Config", string.Empty, "Copy an MCP client config snippet and use token placeholders/env.");
            m_checklistPermissionsPolicy = AddChecklistRow(checklistSection, "Permissions policy", "recommended", "Create config/policy files or choose a profile in Permissions; Readonly is only a temporary smoke-test profile.");
            m_checklistCatalogPolicySummary = AddChecklistRow(checklistSection, "Catalog policy summary", string.Empty, "Refresh after Gateway/runtime updates to see command catalog totals and policy ask/deny counts.");
            m_checklistCatalogPolicySummary.enableRichText = true;
            m_checklistRoslynCompiler = AddChecklistRow(checklistSection, "Roslyn Compiler", "optional", "Install only when dynamic C# validation commands are needed.");
            m_checklistUnityAiAssistant = AddChecklistRow(checklistSection, "Unity AI Assistant", "optional", "Optional package for Unity Assistant workflows; SGGMcp baseline does not require it.");

            var registrySection = AddSection(m_setupPanel, "Required CanonicalPath");
            HelpRow(registrySection, "CanonicalPath is required for bridge discovery and path-bound MCP commands. Add the scoped registry first, then install CanonicalPath from npmjs. This setup window still compiles before CanonicalPath is installed.");
            m_setupCanonicalPathRegistry = AddRow(registrySection, "CanonicalPath registry");
            m_setupCanonicalPathRegistry.name = "setup-canonical-path-registry";
            m_setupCanonicalPathDependency = AddRow(registrySection, "CanonicalPath dependency");
            m_setupCanonicalPathDependency.name = "setup-canonical-path-dependency";
            m_setupCanonicalPathManifest = AddRow(registrySection, "Manifest");
            m_setupCanonicalPathManifest.name = "setup-manifest";
            var registryActions = AddActions(registrySection);
            m_addCanonicalPathRegistryButton = AddButton(registryActions, "Add Scoped Registry", () => RunAction("Add Scoped Registry", ConfigureCanonicalPathRegistry));
            m_addCanonicalPathRegistryButton.name = "setup-add-canonical-path-registry";
            m_installCanonicalPathButton = AddButton(registryActions, "Install CanonicalPath", InstallCanonicalPathPackage);
            AddButton(registryActions, "Copy Manifest Snippet", CopyCanonicalPathManifestSnippet);
            AddButton(registryActions, "Open manifest.json", OpenProjectManifest);
            AddButton(registryActions, "Refresh", () => RefreshAll());

            var nodeSection = AddSection(m_setupPanel, "Portable Node");
            m_nodeIndicator = AddStatusLine(nodeSection, out m_nodeStatus);
            m_nodeVersion = AddRow(nodeSection, "Version");
            m_nodeVersion.name = "setup-node-version";
            m_nodePath = AddRow(nodeSection, "Node Path");
            m_nodePath.name = "setup-node-path";
            m_npmPath = AddRow(nodeSection, "npm Path");
            m_npmPath.name = "setup-npm-path";
            var nodeActions = AddActions(nodeSection);
            m_installPortableNodeButton = AddButton(nodeActions, "Install Portable Node", () => RunAction("Install Portable Node", () => NodeRuntimeInstaller.InstallPortableNode(true)));
            m_installPortableNodeButton.name = "setup-install-portable-node";
            AddButton(nodeActions, "Refresh", () => RefreshAll());

            var gatewaySection = AddSection(m_setupPanel, "Gateway");
            m_gatewayIndicator = AddStatusLine(gatewaySection, out m_gatewayStatus);
            m_gatewayPath = AddRow(gatewaySection, "Repo Path");
            m_gatewayEntry = AddRow(gatewaySection, "Entrypoint");
            var gatewayActions = AddActions(gatewaySection);
            m_installRuntimeButton = AddButton(gatewayActions, "Install Runtime", () => RunAction("Install Gateway Runtime", InstallGatewayRuntime, true));
            m_installRuntimeButton.name = "setup-install-runtime";
            AddButton(gatewayActions, "Refresh", () => RefreshAll(true));

            var processSection = AddSection(m_setupPanel, "Gateway Process");
            m_processIndicator = AddStatusLine(processSection, out m_processStatus);
            m_processHealth = AddRow(processSection, "Health URL");
            m_processBody = AddRow(processSection, "Health Summary");
            m_processLogPath = AddRow(processSection, "Log File");
            m_processLogTail = AddRow(processSection, "Log Tail");
            var processActions = AddActions(processSection);
            m_startGatewayButton = AddButton(processActions, "Start Gateway", StartOrRestartGateway);
            m_stopGatewayButton = AddButton(processActions, "Stop Gateway", StopGateway);
            m_setupRefreshHealthButton = AddButton(processActions, "Refresh Health", RefreshHealthAndChecklist);
            AddButton(processActions, "Copy Log", CopyGatewayLog);

            BuildTestHistorySection(m_setupPanel);

            var tokenSection = AddSection(m_setupPanel, "MCP Token");
            AddRow(tokenSection, "Safety", "Token values stay in memory/process environment only. This UI never writes token values to files or diagnostics, and copy helpers use env/placeholder text.");
            m_setupMcpTokenEnv = AddRow(tokenSection, "Token env", McpTokenEnvName);
            m_setupMcpTokenStatus = AddRow(tokenSection, "Token status");
            m_setupMcpClientHeader = AddRow(tokenSection, "Client header", "X-SGG-Unity-MCP-Token: ${" + McpTokenEnvName + "}");
            m_setupMcpSessionToken = SecretInputRow(tokenSection, "Session token input");
            var tokenActions = AddActions(tokenSection);
            AddButton(tokenActions, "Generate Token", GenerateSessionToken);
            AddButton(tokenActions, "Use Token For This Session", UseSessionToken);
            AddButton(tokenActions, "Copy Client Header", CopyClientHeaderPlaceholder);
            AddButton(tokenActions, "Copy Env Command", CopyTokenEnvCommandPlaceholder);
            AddButton(tokenActions, "Clear Session Token", ClearSessionToken);

            var connectionSection = AddSection(m_setupPanel, "MCP Connection");
            HelpRow(connectionSection, "Choose how OpenCode in WSL reaches the Windows Gateway. Stdio shim keeps Gateway on Windows loopback; Remote HTTP opens Gateway to a trusted local interface and requires token/firewall controls.");
            var connectionModes = new List<string> { GatewayNetworkModeManager.ModeWindowsStdioLabel, GatewayNetworkModeManager.ModeRemoteHttpLabel };
            m_mcpConnectionModeField = new PopupField<string>("Connection mode", connectionModes, ConnectionModeIndex(CurrentConnectionMode()));
            m_mcpConnectionModeField.name = "setup-connection-mode";
            m_mcpConnectionModeField.AddToClassList("mcp-input-row");
            m_mcpConnectionModeField.AddToClassList("mcp-input-field");
            m_mcpConnectionModeField.RegisterValueChangedCallback(evt => OnMcpConnectionModeChanged(evt.newValue));
            connectionSection.Add(m_mcpConnectionModeField);
            m_mcpConnectionSelectedMode = AddRow(connectionSection, "Selected mode");
            m_mcpConnectionGatewayBind = AddRow(connectionSection, "Gateway bind");
            m_mcpConnectionMcpUrl = AddRow(connectionSection, "MCP URL");
            m_mcpConnectionWslUrl = AddRow(connectionSection, "WSL URL");
            m_mcpConnectionTokenStatus = AddRow(connectionSection, "Token status");
            m_mcpConnectionSecurityStatus = AddRow(connectionSection, "Security status");
            m_mcpConnectionConfigPath = AddRow(connectionSection, "Gateway config");
            m_mcpConnectionOpenCodeWorkspaceRoot = new TextField("OpenCode workspace root")
            {
                name = "setup-opencode-workspace-root",
                isDelayed = true,
            };
            m_mcpConnectionOpenCodeWorkspaceRoot.AddToClassList("mcp-input-row");
            m_mcpConnectionOpenCodeWorkspaceRoot.AddToClassList("mcp-input-field");
            m_mcpConnectionOpenCodeWorkspaceRoot.RegisterValueChangedCallback(evt => OnOpenCodeWorkspaceRootChanged(evt.newValue));
            connectionSection.Add(m_mcpConnectionOpenCodeWorkspaceRoot);
            var workspaceActions = AddActions(connectionSection);
            var workspaceAutoButton = AddButton(workspaceActions, "Auto", ResetOpenCodeWorkspaceRoot);
            workspaceAutoButton.name = "setup-opencode-workspace-auto";
            var workspaceBrowseButton = AddButton(workspaceActions, "Browse", BrowseOpenCodeWorkspaceRoot);
            workspaceBrowseButton.name = "setup-opencode-workspace-browse";
            m_mcpConnectionOpenCodePath = AddRow(connectionSection, "OpenCode config");
            m_mcpConnectionOpenCodeStatus = AddRow(connectionSection, "Config target status");
            m_mcpConnectionOpenCodeStatus.name = "setup-opencode-config-status";
            m_mcpConnectionReportedWorkspace = AddRow(connectionSection, "Reported workspace verification");
            m_mcpConnectionReportedWorkspace.name = "setup-opencode-reported-workspace";
            AddRow(connectionSection, "Remote HTTP warning", "Remote HTTP exposes the Gateway outside Windows loopback. Use only on trusted local development machines with token auth and Windows Firewall restrictions.");
            var connectionActions = AddActions(connectionSection);
            AddButton(connectionActions, "Apply Stdio Shim Mode", () => RunAction("Apply Stdio Shim Mode", GatewayNetworkModeManager.ApplyLoopbackMode, true));
            AddButton(connectionActions, "Apply Remote HTTP Mode", () => RunAction("Apply Remote HTTP Mode", GatewayNetworkModeManager.ApplyRemoteHttpMode, true));
            var connectionRestartGatewayButton = AddButton(connectionActions, "Restart Gateway", RestartGateway);
            m_restartGatewayButtons.Add(connectionRestartGatewayButton);
            AddButton(connectionActions, "Copy OpenCode Config", CopyOpenCodeConfig);
            AddButton(connectionActions, "Copy MCP URL", CopySelectedMcpUrl);
            AddButton(connectionActions, "Copy Firewall Rule", CopyFirewallRule);
            AddButton(connectionActions, "Preview OpenCode Config Change", PreviewOpenCodeConfigChange);
            AddButton(connectionActions, "Create/Update OpenCode Config", CreateOrUpdateOpenCodeConfig);
            AddButton(connectionActions, "Open OpenCode Config", () => RunAction("Open OpenCode Config", GatewayNetworkModeManager.OpenOpenCodeConfig));
            AddButton(connectionActions, "Refresh Connection", RefreshOpenCodeConnection);

            var clientSection = AddSection(m_setupPanel, "MCP Client Config");
            m_setupMcpUrl = AddRow(clientSection, "MCP URL", McpUrl);
            m_setupHealthUrl = AddRow(clientSection, "Health URL", HealthUrl);
            m_setupProjectId = AddRow(clientSection, "Project Id", ProjectPaths.ProjectId);
            AddRow(clientSection, "Token placeholder", "Use ${" + McpTokenEnvName + "} or <YOUR_MCP_TOKEN>; snippets do not include real token values.");
            var clientActions = AddActions(clientSection);
            AddButton(clientActions, "Copy MCP URL", () => CopyText("MCP URL", McpUrl));
            AddButton(clientActions, "Copy OpenCode Config", CopyOpenCodeConfig);
            AddButton(clientActions, "Copy Cursor Config", CopyCursorConfig);
            AddButton(clientActions, "Copy Assistant Config", CopyAssistantConfig);
            AddButton(clientActions, "Copy First Calls", CopyFirstCalls);

            var smokeTestSection = AddSection(m_setupPanel, "First MCP Smoke Test");
            AddRow(smokeTestSection, "Goal", "Confirm the client can see this Unity project and run one read-only status command.");
            AddRow(smokeTestSection, "Calls", "sgg_unity_projects -> sgg_unity_describe(selector=\"commands\") -> compile.status");
            AddRow(smokeTestSection, "Expected result", "compile.status returns editor compile state without mutating project assets.");
            AddRow(smokeTestSection, "After success", "Open Permissions to choose a broader profile only for the workflow you need.");
            var smokeTestActions = AddActions(smokeTestSection);
            AddButton(smokeTestActions, "Copy Smoke Test Calls", CopySmokeTestCalls);

            var preferencesSection = AddSection(m_setupPanel, "MCP Window Preferences");
            HelpRow(preferencesSection, "Language affects only this MCP Window. Gateway responses, command IDs, config snippets, paths and logs remain unchanged.");
            var languageNames = McpWindowLocalization.LanguageDisplayNames();
            var languageIndex = Math.Min(McpWindowLocalization.LanguageIndex(McpWindowLocalization.CurrentLanguage), languageNames.Count - 1);
            m_languageField = new PopupField<string>("Language", languageNames, Math.Max(0, languageIndex));
            m_languageField.AddToClassList("mcp-input-row");
            m_languageField.AddToClassList("mcp-input-field");
            m_languageField.RegisterValueChangedCallback(evt => OnLanguageChanged(evt.newValue));
            preferencesSection.Add(m_languageField);
            m_setupLanguageCurrent = AddRow(preferencesSection, "Current language", McpWindowLocalization.CurrentLanguageDisplayName());
            AddRow(preferencesSection, "Scope", "Only MCP Window UI text; gateway/runtime output and generated snippets stay unchanged.");

            var firstPermissionsSection = AddSection(m_setupPanel, "Permission Setup");
            AddRow(firstPermissionsSection, "Policy config", "Create user-editable Gateway config and policy files so MCP permissions are explicit for this Project Id.");
            AddRow(firstPermissionsSection, "Readonly", "Optional first smoke-test profile: safe for connection/status checks, but too limited for real project work.");
            AddRow(firstPermissionsSection, "Next action", "Open Permissions to choose a profile; use Readonly only briefly, then enable the feature flags and policy profile your workflow needs.");
            var firstPermissionsActions = AddActions(firstPermissionsSection);
            AddButton(firstPermissionsActions, "Create Config/Policy Files", () => RunAction("Create Config/Policy Files", GatewayPolicyManager.EnsureUserConfig));
            AddButton(firstPermissionsActions, "Apply Readonly (optional)", () => ConfirmApplyProfile(GatewayPolicyManager.ReadonlyProfile, "Readonly"));
            var setupRestartGatewayButton = AddButton(firstPermissionsActions, "Restart Gateway", RestartGateway);
            m_restartGatewayButtons.Add(setupRestartGatewayButton);
            AddButton(firstPermissionsActions, "Open Permissions", () => SelectTab("Permissions"));

            var optionalToolsSection = AddSection(m_setupPanel, "Optional Tools");
            HelpRow(optionalToolsSection, "Optional tools are not required for the first MCP connection. Install them only when their command families are needed.");
            m_setupRoslynStatus = OptionalToolRow(optionalToolsSection, "Roslyn status");
            m_setupRoslynPath = OptionalToolRow(optionalToolsSection, "Roslyn path", EditorRoslynInstaller.ProjectRelativeFolder);
            m_setupRoslynFeature = OptionalToolRow(optionalToolsSection, "Roslyn feature", "Dynamic C# compile/validation helpers; not required for read-only baseline.");
            m_setupRoslynPolicyRisk = OptionalToolRow(optionalToolsSection, "Roslyn policy risk", "Execute-risk command family; keep disabled until explicitly needed and allowed by policy.");
            var roslynActions = AddActions(optionalToolsSection);
            AddButton(roslynActions, "Install Roslyn Compiler", () => RunAction("Install Roslyn Compiler", InstallRoslynCompiler));
            AddButton(roslynActions, "Uninstall Roslyn Compiler", () => RunAction("Uninstall Roslyn Compiler", UninstallRoslynCompiler));
            AddButton(roslynActions, "Copy Roslyn Status", CopyRoslynStatus);
            AddButton(roslynActions, "Open Commands Filter", OpenRoslynCommandsFilter);
            m_setupUnityAiSummary = OptionalToolRow(optionalToolsSection, "Unity AI Assistant", "Optional baseline: local SGGMcp does not require Unity Assistant chat, Unity Cloud, credits, subscription or official relay.");
            var unityAiActions = AddActions(optionalToolsSection);
            AddButton(unityAiActions, "Install Unity AI Assistant (optional)", InstallUnityAiAssistantPackage);
            AddButton(unityAiActions, "Open Unity AI Details", () => SelectTab("Unity AI Assistant"));
            AddButton(unityAiActions, "Copy Package Id", () => CopyText("Unity AI Assistant package id", UnityAiAssistantPackageId));
            BuildOpenCodePresentationSetup(optionalToolsSection);
            BuildUnityCliPipelineSection(optionalToolsSection);
        }

        void BuildStatusPanel()
        {
            HelpRow(m_statusPanel, "Status is read-only health and connection visibility. Refresh Health probes the local Gateway and Copy Log only copies diagnostics for troubleshooting.");

            var bridgeSection = AddSection(m_statusPanel, "Unity Bridge");
            m_bridgeIndicator = AddStatusLine(bridgeSection, out m_bridgeStatus);
            m_bridgeProject = AddRow(bridgeSection, "Project");
            m_bridgePort = AddRow(bridgeSection, "Bridge Port");
            m_heartbeatPath = AddRow(bridgeSection, "Heartbeat");
            m_heartbeatLastWrite = AddRow(bridgeSection, "Last Write");
            m_statusConnectedProject = AddRow(bridgeSection, "Connected Project");
            m_statusCommandsHash = AddRow(bridgeSection, "Commands hash");
            m_statusHeartbeatAge = AddRow(bridgeSection, "Last heartbeat age");
            m_statusBridgeToken = AddRow(bridgeSection, "Bridge token");

            var runtimeSection = AddSection(m_statusPanel, "Runtime Summary");
            m_statusFeatureFlags = AddRow(runtimeSection, "Feature flags");
            m_statusPolicyProfile = AddRow(runtimeSection, "Policy profile");
            m_statusGatewayRuntimeVersion = AddRow(runtimeSection, "Gateway runtime version");
            m_statusUnityPackageVersion = AddRow(runtimeSection, "Unity package version");
            m_statusCanonicalPathVersion = AddRow(runtimeSection, "CanonicalPath package version");
            m_statusMcpToken = AddRow(runtimeSection, "MCP token");
            m_statusRuntimePolicy = AddRow(runtimeSection, "Runtime policy");
            AddRow(runtimeSection, "Status Legend", "✔ Active/Success  ▲ Warning/Caution  ● Error/Danger  ◇ Optional");
            AddRow(runtimeSection, "Token rows", "Bridge token displays Present/Missing; MCP token displays Present/Not configured; values are never shown or copied.");

            var gatewaySection = AddSection(m_statusPanel, "Gateway Health");
            m_statusProcessIndicator = AddStatusLine(gatewaySection, out m_statusProcessStatus);
            m_statusProcessHealth = AddRow(gatewaySection, "Health URL");
            m_statusProcessBody = AddRow(gatewaySection, "Health Summary");
            m_statusProcessLogPath = AddRow(gatewaySection, "Log File");
            m_statusProcessLogTail = AddRow(gatewaySection, "Log Tail");
            var gatewayActions = AddActions(gatewaySection);
            m_statusRefreshHealthButton = AddButton(gatewayActions, "Refresh Health", RefreshHealthAndChecklist);
            AddButton(gatewayActions, "Copy Log", CopyGatewayLog);

            var endpointSection = AddSection(m_statusPanel, "MCP Client Endpoint");
            AddRow(endpointSection, "MCP URL", "http://127.0.0.1:27182/mcp");
            AddRow(endpointSection, "Health URL", "http://127.0.0.1:27182/health");
            AddRow(endpointSection, "Project Id", ProjectPaths.ProjectId);
        }

        void GenerateSessionToken()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            m_setupMcpSessionToken.value = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            m_lastActionLabel.text = "Generated session token in the secret input only. Use Token For This Session to set process environment.";
            RefreshMcpTokenSetup();
        }

        void UseSessionToken()
        {
            var token = m_setupMcpSessionToken == null ? string.Empty : m_setupMcpSessionToken.value;
            if (string.IsNullOrWhiteSpace(token))
            {
                m_lastActionLabel.text = "Use Token For This Session: token input is empty.";
                return;
            }

            Environment.SetEnvironmentVariable(McpTokenEnvName, token.Trim());
            m_lastActionLabel.text = "MCP token set for this Unity Editor process only. Token value was not copied or written to files.";
            RefreshAll();
        }

        void ClearSessionToken()
        {
            if (m_setupMcpSessionToken != null)
            {
                m_setupMcpSessionToken.value = string.Empty;
            }

            Environment.SetEnvironmentVariable(McpTokenEnvName, null);
            m_lastActionLabel.text = "Session token input and process environment token cleared.";
            RefreshAll();
        }

        void CopyClientHeaderPlaceholder()
        {
            CopyText("Client header", "X-SGG-Unity-MCP-Token: ${" + McpTokenEnvName + "}");
        }

        void CopyTokenEnvCommandPlaceholder()
        {
            CopyText("Token env command", "# PowerShell session only; replace placeholder before running\n$env:" + McpTokenEnvName + " = \"<YOUR_MCP_TOKEN>\"");
        }

        void CopyOpenCodeConfig()
        {
            CopyText("OpenCode MCP config", GatewayNetworkModeManager.OpenCodeConfigSnippet(CurrentConnectionMode()));
        }

        void CopySelectedMcpUrl()
        {
            CopyText("MCP URL", GatewayNetworkModeManager.GetStatus(CurrentConnectionMode()).McpUrl);
        }

        void CopyFirewallRule()
        {
            CopyText("Windows Firewall rule", GatewayNetworkModeManager.FirewallRuleCommand());
        }

        void PreviewOpenCodeConfigChange()
        {
            RunAction("Preview OpenCode Config Change", () => GatewayNetworkModeManager.PreviewOpenCodeConfig(CurrentConnectionMode()));
        }

        void OnOpenCodeWorkspaceRootChanged(string workspaceRoot)
        {
            RunAction("Set OpenCode Workspace Root", () => GatewayNetworkModeManager.SetOpenCodeWorkspaceRoot(workspaceRoot));
        }

        void ResetOpenCodeWorkspaceRoot()
        {
            RunAction("Use Automatic OpenCode Workspace", GatewayNetworkModeManager.ResetOpenCodeWorkspaceRoot);
        }

        void BrowseOpenCodeWorkspaceRoot()
        {
            var status = GatewayNetworkModeManager.GetStatus(CurrentConnectionMode());
            var initialFolder = Directory.Exists(status.OpenCodeWorkspaceRoot) ? status.OpenCodeWorkspaceRoot : ProjectPaths.ProjectRoot;
            var selectedFolder = EditorUtility.OpenFolderPanel(McpWindowLocalization.Text("Select OpenCode Workspace Root"), initialFolder, string.Empty);
            if (string.IsNullOrEmpty(selectedFolder))
            {
                return;
            }

            RunAction("Set OpenCode Workspace Root", () => GatewayNetworkModeManager.SetOpenCodeWorkspaceRoot(selectedFolder));
        }

        void RefreshOpenCodeConnection()
        {
            GatewayNetworkModeManager.InvalidateOpenCodeGitRootCache();
            RefreshAll(true);
        }

        void CreateOrUpdateOpenCodeConfig()
        {
            var mode = CurrentConnectionMode();
            var status = GatewayNetworkModeManager.GetStatus(mode);
            string confirmedFallbackConfigPath = null;
            if (status.OpenCodeRequiresConfirmation)
            {
                var message = "No containing Git worktree or saved OpenCode workspace is available.\n\n"
                    + "Unity project: " + ProjectPaths.ProjectRoot + "\n"
                    + "OpenCode config target: " + status.OpenCodePath + "\n\n"
                    + "Confirm only if OpenCode opens this Unity project folder as its workspace.";
                if (!EditorUtility.DisplayDialog("Confirm OpenCode Config Target", message, "Create/Update", "Cancel"))
                {
                    return;
                }

                confirmedFallbackConfigPath = status.OpenCodePath;
            }

            RunAction("Create/Update OpenCode Config", () => GatewayNetworkModeManager.WriteOpenCodeConfig(mode, status.OpenCodePath, confirmedFallbackConfigPath));
        }

        string CurrentConnectionMode()
        {
            return GatewayNetworkModeManager.NormalizeMode(EditorPrefs.GetString(McpConnectionModePrefsKey, GatewayNetworkModeManager.ModeWindowsStdio));
        }

        int ConnectionModeIndex(string mode)
        {
            return GatewayNetworkModeManager.NormalizeMode(mode) == GatewayNetworkModeManager.ModeRemoteHttp ? 1 : 0;
        }

        void OnMcpConnectionModeChanged(string label)
        {
            var mode = GatewayNetworkModeManager.NormalizeMode(label);
            if (mode == CurrentConnectionMode())
            {
                return;
            }

            EditorPrefs.SetString(McpConnectionModePrefsKey, mode);
            RefreshAll(false);
        }

        void CopyCursorConfig()
        {
            CopyText("Cursor MCP config", "{\n"
                + "  \"mcpServers\": {\n"
                + "    \"sgg-unity-mcp\": {\n"
                + "      \"command\": \"sgg-unity-mcp-gateway\",\n"
                + "      \"args\": [\"stdio-shim\", \"--url\", \"" + McpUrl + "\", \"--token-env\", \"" + McpTokenEnvName + "\"],\n"
                + "      \"env\": { \"" + McpTokenEnvName + "\": \"<YOUR_MCP_TOKEN>\" }\n"
                + "    }\n"
                + "  }\n"
                + "}");
        }

        void CopyAssistantConfig()
        {
            CopyText("Assistant MCP config", "{\n"
                + "  \"enabled\": true,\n"
                + "  \"path\": \"\",\n"
                + "  \"mcpServers\": {\n"
                + "    \"sgg-unity-mcp\": {\n"
                + "      \"type\": \"stdio\",\n"
                + "      \"command\": \"sgg-unity-mcp-gateway\",\n"
                + "      \"args\": [\"stdio-shim\", \"--url\", \"" + McpUrl + "\", \"--token-env\", \"" + McpTokenEnvName + "\"],\n"
                + "      \"env\": { \"" + McpTokenEnvName + "\": \"<YOUR_MCP_TOKEN>\" }\n"
                + "    }\n"
                + "  }\n"
                + "}");
        }

        void CopyFirstCalls()
        {
            CopyText("First MCP calls", SmokeTestCallsText());
        }

        void CopySmokeTestCalls()
        {
            CopyText("First MCP smoke test calls", SmokeTestCallsText());
        }

        string SmokeTestCallsText()
        {
            return "1. sgg_unity_projects\n"
                + "2. sgg_unity_describe(project=\"" + ProjectPaths.ProjectId + "\", selector=\"commands\")\n"
                + "3. sgg_unity_call(project=\"" + ProjectPaths.ProjectId + "\", command=\"compile.status\", args_json=\"{}\", mode=\"wait\")\n"
                + "Expected: compile.status returns read-only editor compile state.";
        }

        NodeRuntimeInstaller.InstallResult ConfigureCanonicalPathRegistry()
        {
            var manifestPath = ProjectManifestPath();
            var message = "Add the Unity Package Manager scoped registry for required CanonicalPath in this project's Packages/manifest.json?\n\n"
                + "This writes the com.romanilyin scope with " + CanonicalPathRegistryUrl + ". Install " + CanonicalPathPackageId + "@" + CanonicalPathVersion + " after the registry is configured.";
            if (!EditorUtility.DisplayDialog("Add CanonicalPath Scoped Registry", message, "Add Registry", "Cancel"))
            {
                return NodeRuntimeInstaller.InstallResult.Ok("cancelled");
            }

            if (!File.Exists(manifestPath))
            {
                return NodeRuntimeInstaller.InstallResult.Fail("Project manifest not found: " + manifestPath);
            }

            var source = File.ReadAllText(manifestPath);
            if (!TryEnsureCanonicalPathScopedRegistry(source, out var updatedSource, out var changed, out var detail))
            {
                return NodeRuntimeInstaller.InstallResult.Fail(detail);
            }

            if (changed)
            {
                File.WriteAllText(manifestPath, updatedSource);
            }

            UnityEditor.PackageManager.Client.Resolve();
            return NodeRuntimeInstaller.InstallResult.Ok((changed ? "Updated " : "Already configured in ") + "Packages/manifest.json. Package Manager resolve requested. " + detail);
        }

        void CopyCanonicalPathManifestSnippet()
        {
            CopyText("CanonicalPath manifest snippet", CanonicalPathManifestSnippet());
        }

        void InstallCanonicalPathPackage()
        {
            var packageSpec = CanonicalPathPackageId + "@" + CanonicalPathVersion;
            var state = ReadCanonicalPathDependencyState();
            if (!state.CanApply)
            {
                m_lastActionLabel.text = CanonicalPathDependencyStateText(state);
                RefreshCanonicalPathRegistry();
                return;
            }

            if (!IsCanonicalPathRegistryConfigured())
            {
                var message = "CanonicalPath is required for bridge discovery and path-bound MCP commands. Installing " + packageSpec + " from npmjs requires the " + CanonicalPathScope + " scoped registry in Packages/manifest.json.";
                if (EditorUtility.DisplayDialog("Install CanonicalPath", message, "Add Registry", "Cancel"))
                {
                    var result = ConfigureCanonicalPathRegistry();
                    m_lastActionLabel.text = "Add Scoped Registry: " + result.Message;
                }
                else
                {
                    m_lastActionLabel.text = "Install CanonicalPath: cancelled; scoped registry is not configured.";
                }

                RefreshCanonicalPathRegistry();
                return;
            }

            var action = state.IsUpdate ? "Update CanonicalPath" : "Install CanonicalPath";
            var manifestPath = ProjectManifestPath();
            var installMessage = CanonicalPathDependencyStateText(state) + "\n\n"
                + McpWindowLocalization.Format("Apply {0} to this Unity project ({1})? Unity may reload assemblies.", packageSpec, Application.productName);
            if (!EditorUtility.DisplayDialog(McpWindowLocalization.Text(action), installMessage,
                McpWindowLocalization.Text(state.IsUpdate ? "Update" : "Install"), McpWindowLocalization.Text("Cancel")))
            {
                m_lastActionLabel.text = McpWindowLocalization.Text("CanonicalPath change cancelled; no package request was sent.");
                return;
            }

            if (!string.Equals(manifestPath, ProjectManifestPath(), StringComparison.Ordinal)
                || !state.SameEvidence(ReadCanonicalPathDependencyState()) || !IsCanonicalPathRegistryConfigured())
            {
                m_lastActionLabel.text = McpWindowLocalization.Text("CanonicalPath state changed during confirmation; refresh Setup and confirm again. No package request was sent.");
                RefreshCanonicalPathRegistry();
                return;
            }

            try
            {
                m_canonicalPathInstallRequest = UnityEditor.PackageManager.Client.Add(packageSpec);
                EditorApplication.update -= PollCanonicalPathInstall;
                EditorApplication.update += PollCanonicalPathInstall;
                m_lastActionLabel.text = McpWindowLocalization.Format("CanonicalPath package change requested: {0}", packageSpec);
                RefreshCanonicalPathRegistry();
            }
            catch (Exception exception)
            {
                m_canonicalPathInstallRequest = null;
                m_lastActionLabel.text = McpWindowLocalization.Text(action) + ": " + exception.Message;
                EditorUtility.DisplayDialog(McpWindowLocalization.Format("{0} Failed", McpWindowLocalization.Text(action)),
                    exception.Message, McpWindowLocalization.Text("OK"));
            }
        }

        void PollCanonicalPathInstall()
        {
            if (m_canonicalPathInstallRequest == null || !m_canonicalPathInstallRequest.IsCompleted)
            {
                return;
            }

            EditorApplication.update -= PollCanonicalPathInstall;
            if (m_canonicalPathInstallRequest.Status == UnityEditor.PackageManager.StatusCode.Success)
            {
                m_lastActionLabel.text = McpWindowLocalization.Format("CanonicalPath package changed to {0}. Wait for Unity to reload assemblies.", m_canonicalPathInstallRequest.Result.version);
            }
            else
            {
                var errorMessage = m_canonicalPathInstallRequest.Error == null ? "unknown Package Manager error" : m_canonicalPathInstallRequest.Error.message;
                m_lastActionLabel.text = McpWindowLocalization.Text("CanonicalPath package change failed: ") + errorMessage;
            }

            m_canonicalPathInstallRequest = null;
            RefreshAll(true);
        }

        void OpenProjectManifest()
        {
            var manifestPath = ProjectManifestPath();
            if (!File.Exists(manifestPath))
            {
                m_lastActionLabel.text = "Open manifest.json: file not found: " + manifestPath;
                EditorUtility.DisplayDialog("Open manifest.json", "Project manifest not found:\n" + manifestPath, "OK");
                return;
            }

            EditorUtility.RevealInFinder(manifestPath);
            m_lastActionLabel.text = "Opened project manifest location: " + manifestPath;
        }

        NodeRuntimeInstaller.InstallResult InstallRoslynCompiler()
        {
            return WrapRoslynResult(EditorRoslynInstaller.Install(true));
        }

        NodeRuntimeInstaller.InstallResult UninstallRoslynCompiler()
        {
            return WrapRoslynResult(EditorRoslynInstaller.Uninstall());
        }

        NodeRuntimeInstaller.InstallResult WrapRoslynResult(EditorRoslynInstaller.InstallResult result)
        {
            return result.Success
                ? NodeRuntimeInstaller.InstallResult.Ok(result.Message)
                : NodeRuntimeInstaller.InstallResult.Fail(result.Message);
        }

        void CopyRoslynStatus()
        {
            CopyText("Roslyn Status", RoslynStatusText() + "\nPath: " + EditorRoslynInstaller.ProjectRelativeFolder + "\nFeature: Dynamic C# compile/validation helpers\nPolicy risk: execute-risk; optional and policy-gated");
        }

        NodeRuntimeInstaller.InstallResult InstallGatewayRuntime()
        {
            var gatewayStatus = GatewayInstaller.GetStatus();
            if (GatewayRuntimeDowngradeAvailable(gatewayStatus))
            {
                var installedVersion = gatewayStatus.InstalledRuntimeVersion;
                var packageVersion = gatewayStatus.PackageRuntimeVersion;
                var message = "The installed Gateway runtime " + installedVersion + " is newer than this Unity package runtime " + packageVersion + ".\n\nDowngrading can remove newer Gateway fixes and capabilities for every connected Unity host. The action remains available only if you explicitly confirm this version transition.";
                if (!EditorUtility.DisplayDialog("Downgrade Gateway Runtime", message, "Downgrade", "Cancel"))
                {
                    return NodeRuntimeInstaller.InstallResult.Ok("Gateway runtime downgrade cancelled; no files changed.");
                }

                var downgradeResult = GatewayInstaller.InstallSourceDependencies(GatewayInstaller.CreateRuntimeDowngradeConfirmation(gatewayStatus));
                m_lastGatewayRuntimeError = downgradeResult.Success ? string.Empty : downgradeResult.Message;
                return downgradeResult;
            }

            var result = GatewayInstaller.InstallSourceDependencies();
            m_lastGatewayRuntimeError = result.Success ? string.Empty : result.Message;
            return result;
        }

        void RefreshHealthAndChecklist()
        {
            var details = new StringBuilder();
            var total = Stopwatch.StartNew();
            GatewayProcessManager.GatewayRuntimeStatus processStatus = null;
            GatewayNetworkModeManager.NetworkStatus networkStatus = null;
            TrackMcpWindowStep(details, "heartbeat", s_mcpWindowHeartbeatMarker, () => ForceHeartbeatBeforeHealthProbe(true));
            TrackMcpWindowStep(details, "health", s_mcpWindowHealthCheckMarker, () => processStatus = RefreshProcess(true));
            RefreshHeartbeatDependentViews(processStatus, details, "heartbeat_views.");
            TrackMcpWindowStep(details, "mcp_connection", () => networkStatus = RefreshMcpConnection());
            RefreshFtue(processStatus, networkStatus);
            total.Stop();
            SetMcpWindowTimingSummary("RefreshHealth", total.ElapsedMilliseconds, details);
        }

        void StartOrRestartGateway()
        {
            RunGatewayProcessAction("Start or Restart Gateway", false, false);
        }

        void CopyGatewayLog()
        {
            var status = GatewayProcessManager.GetStatus(false);
            try
            {
                var logText = !string.IsNullOrEmpty(status.LogPath) && File.Exists(status.LogPath)
                    ? File.ReadAllText(status.LogPath)
                    : status.LogTail;
                GUIUtility.systemCopyBuffer = "SGG Unity MCP Gateway Log\n"
                    + "Status: " + status.Message + "\n"
                    + "Health URL: " + status.HealthUrl + "\n"
                    + "Log File: " + (string.IsNullOrEmpty(status.LogPath) ? status.LogDirectory : status.LogPath) + "\n\n"
                    + (string.IsNullOrWhiteSpace(logText) ? "No gateway log output" : logText.Trim());
                m_lastActionLabel.text = "Gateway log copied to clipboard.";
            }
            catch (Exception exception)
            {
                m_lastActionLabel.text = "Copy Log: " + exception.Message;
                EditorUtility.DisplayDialog("Copy Log Failed", exception.Message, "OK");
            }
        }

        void RefreshCanonicalPathRegistry()
        {
            if (m_setupCanonicalPathRegistry == null)
            {
                return;
            }

            m_setupCanonicalPathRegistry.text = CanonicalPathRegistryStatusText();
            m_setupCanonicalPathDependency.text = CanonicalPathDependencyStatusText();
            m_setupCanonicalPathManifest.text = ProjectManifestPath();
            UpdateCanonicalPathActionButtons();
        }

        void UpdateCanonicalPathActionButtons()
        {
            var registryNeeded = !IsCanonicalPathRegistryConfigured();
            var state = ReadCanonicalPathDependencyState();
            SetActionNeeded(m_addCanonicalPathRegistryButton, registryNeeded, "Add the com.romanilyin scoped registry required before installing CanonicalPath from npmjs.");
            if (m_installCanonicalPathButton != null)
            {
                m_installCanonicalPathButton.text = McpWindowLocalization.Text(state.IsUpdate ? "Update CanonicalPath" : "Install CanonicalPath");
                m_installCanonicalPathButton.SetEnabled(state.CanApply);
            }
            SetActionNeeded(m_installCanonicalPathButton, state.CanApply, CanonicalPathDependencyStateText(state));
        }

        void RefreshNode()
        {
            if (m_nodeStatus == null)
            {
                return;
            }

            var status = NodeRuntimeInstaller.GetStatus();
            m_lastNodeRuntimeStatus = status;
            m_nodeStatus.text = status.Message;
            m_nodeVersion.text = string.IsNullOrEmpty(status.Version) ? "Not available" : status.Version;
            m_nodePath.text = string.IsNullOrEmpty(status.NodePath) ? status.PortablePath : status.NodePath;
            m_npmPath.text = string.IsNullOrEmpty(status.NpmPath) ? status.PortableNpmPath : status.NpmPath;
            SetIndicator(m_nodeIndicator, status.IsAvailable && status.IsPortable, status.IsAvailable && !status.IsPortable);
            SetActionNeeded(m_installPortableNodeButton, !status.IsPortable, "Install the portable Node runtime used by the Gateway setup window.");
        }

        void RefreshGateway(GatewayProcessManager.GatewayRuntimeStatus processStatusOverride = null)
        {
            if (m_gatewayStatus == null)
            {
                return;
            }

            var status = GatewayInstaller.GetStatus();
            var processStatus = ChecklistProcessStatus(processStatusOverride);
            m_gatewayStatus.text = GatewayRuntimeStatusText(status, processStatus);
            m_gatewayPath.text = string.IsNullOrEmpty(status.RepoRoot) ? "Not found" : "runtime: " + status.RuntimeRoot + "\nsource: " + status.RepoRoot;
            if (status.HasBundledCli)
            {
                m_gatewayEntry.text = status.BundledCliPath;
            }
            else
            {
                m_gatewayEntry.text = File.Exists(status.PackageRuntimeCliPath) ? "Bundled runtime available in package" : "Bundled runtime missing from package";
            }

            SetIndicator(m_gatewayIndicator, GatewayRuntimeIsCurrent(status, processStatus), GatewayRuntimeNeedsAttention(status, processStatus));
            UpdateGatewayActionButtons(status, processStatus);
            if (m_statusGatewayRuntimeVersion != null)
            {
                m_statusGatewayRuntimeVersion.text = GatewayRuntimeVersionSummary(status, processStatus);
            }
        }

        void RefreshProcess()
        {
            if (m_processStatus == null)
            {
                return;
            }

            RefreshProcess(false);
        }

        GatewayProcessManager.GatewayRuntimeStatus RefreshProcess(bool probeHealth)
        {
            var status = GatewayProcessManager.GetStatus(probeHealth);
            m_lastProcessStatus = status;
            if (status.HealthProbed)
            {
                m_lastProbedProcessStatus = status;
            }

            if (m_processStatus == null)
            {
                return status;
            }

            var gatewayStatus = GatewayInstaller.GetStatus();
            var displayStatus = ChecklistProcessStatus(status);
            UpdateGatewayHealthLabels(m_processIndicator, m_processStatus, m_processHealth, m_processBody, m_processLogPath, m_processLogTail, displayStatus, gatewayStatus);
            UpdateGatewayHealthLabels(m_statusProcessIndicator, m_statusProcessStatus, m_statusProcessHealth, m_statusProcessBody, m_statusProcessLogPath, m_statusProcessLogTail, displayStatus, gatewayStatus);
            UpdateRefreshHealthActionButtons(displayStatus);
            return displayStatus;
        }

        void RefreshBridge()
        {
            if (m_bridgeStatus == null)
            {
                return;
            }

            var heartbeatPath = UnityBridgeRuntime.HeartbeatFilePath;
            var heartbeatExists = File.Exists(heartbeatPath);
            m_bridgeStatus.text = UnityBridgeRuntime.Status;
            m_bridgeProject.text = UnityBridgeRuntime.Project;
            m_bridgePort.text = UnityBridgeRuntime.BridgePort <= 0 ? "Not listening" : UnityBridgeRuntime.BridgePort.ToString();
            m_heartbeatPath.text = heartbeatPath;
            m_heartbeatLastWrite.text = heartbeatExists ? File.GetLastWriteTime(heartbeatPath).ToString("u") : "Not written yet";
            if (m_statusConnectedProject != null)
            {
                m_statusConnectedProject.text = UnityBridgeRuntime.Project;
                m_statusCommandsHash.text = string.IsNullOrEmpty(UnityBridgeRuntime.CommandsHash) ? "Not available" : UnityBridgeRuntime.CommandsHash;
                m_statusHeartbeatAge.text = heartbeatExists ? FormatAge(DateTime.UtcNow - File.GetLastWriteTimeUtc(heartbeatPath)) : "Missing";
                m_statusBridgeToken.text = UnityBridgeRuntime.HasBridgeToken ? "Present" : "Missing";
            }

            SetIndicator(m_bridgeIndicator, UnityBridgeRuntime.BridgePort > 0 && heartbeatExists, UnityBridgeRuntime.BridgePort > 0 && !heartbeatExists);
        }

        void RefreshSetupChecklist(GatewayProcessManager.GatewayRuntimeStatus processStatusOverride = null)
        {
            if (m_checklistUnityBridge == null)
            {
                return;
            }

            var nodeStatus = NodeRuntimeInstaller.GetStatus();
            var gatewayStatus = GatewayInstaller.GetStatus();
            var processStatus = ChecklistProcessStatus(processStatusOverride);
            var policyStatus = GatewayPolicyManager.GetStatus();
            m_lastFtueNodeStatus = nodeStatus;
            m_lastFtueGatewayStatus = gatewayStatus;
            m_lastFtuePolicyStatus = policyStatus;
            RefreshUnityBridgeChecklist();
            SetChecklist(m_checklistPortableNode, nodeStatus.IsAvailable && nodeStatus.IsPortable ? "ok" : nodeStatus.IsAvailable ? "warn" : "error", ChecklistText(nodeStatus.IsAvailable, nodeStatus.IsAvailable && !nodeStatus.IsPortable, nodeStatus.Message, "Install Portable Node."));
            if (gatewayStatus.CanStart)
            {
                m_lastGatewayRuntimeError = string.Empty;
                SetChecklist(m_checklistGatewayRuntime, "ok", "Ready - " + gatewayStatus.Message);
            }
            else if (!string.IsNullOrEmpty(m_lastGatewayRuntimeError))
            {
                SetChecklist(m_checklistGatewayRuntime, "error", "Failed - " + Bounded(m_lastGatewayRuntimeError, 220));
            }
            else
            {
                SetChecklist(m_checklistGatewayRuntime, "warn", ChecklistText(false, true, gatewayStatus.Message, "Install Runtime."));
            }

            SetChecklist(m_checklistGatewayProcess, GatewayProcessChecklistState(processStatus, gatewayStatus), GatewayProcessChecklistText(processStatus, gatewayStatus));
            var canonicalPathState = ReadCanonicalPathDependencyState();
            SetChecklist(m_checklistCanonicalPathRegistry, canonicalPathState.IsReady ? "ok" : canonicalPathState.IsBusy ? "warn" : "error", CanonicalPathDependencyStateText(canonicalPathState));
            SetChecklist(m_checklistMcpToken, !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(McpTokenEnvName)) ? "ok" : "warn", ChecklistText(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(McpTokenEnvName)), false, "Present in process environment.", "Generate or paste a session token; optional for localhost."));
            SetChecklist(m_checklistMcpClientConfig, "ok", "Ready - copy OpenCode/Cursor/Assistant config with placeholder token and MCP URL " + McpUrl + ".");
            SetChecklist(m_checklistPermissionsPolicy, policyStatus.UserConfigReady && policyStatus.ProjectPolicyReady ? "ok" : "warn", ChecklistText(policyStatus.UserConfigReady && policyStatus.ProjectPolicyReady, policyStatus.RuntimeReady && (!policyStatus.UserConfigReady || !policyStatus.ProjectPolicyReady), policyStatus.Message, "Create config/policy files or open Permissions to choose a profile; Readonly is optional."));
            var catalogPolicySummaryText = CatalogPolicySummaryText(out var catalogPolicySummaryState);
            SetChecklist(m_checklistCatalogPolicySummary, catalogPolicySummaryState, catalogPolicySummaryText);
            SetChecklist(m_checklistRoslynCompiler, EditorRoslynInstaller.IsInstalled() ? "ok" : "optional", EditorRoslynInstaller.IsInstalled() ? "Ready - " + RoslynStatusText() : "Optional - install only for dynamic C# validation commands.");
            SetChecklist(m_checklistUnityAiAssistant, UnityAiAssistantPackageStatus().StartsWith("Installed:", StringComparison.Ordinal) ? "ok" : "optional", "Optional - " + UnityAiAssistantPackageStatus());
            UpdateRefreshHealthActionButtons(processStatus);
        }

        void RefreshUnityBridgeChecklist()
        {
            if (m_checklistUnityBridge == null)
            {
                return;
            }

            var heartbeatExists = File.Exists(UnityBridgeRuntime.HeartbeatFilePath);
            var bridgeReady = UnityBridgeRuntime.BridgePort > 0 && heartbeatExists;
            SetChecklist(m_checklistUnityBridge, bridgeReady ? "ok" : "error", bridgeReady ? "Ready - Bridge heartbeat active." : "Blocked - Unity bridge heartbeat missing; keep this project open and wait for compile.");
        }

        void RefreshCatalogPolicySummaryAfterHeartbeat()
        {
            if (m_checklistCatalogPolicySummary == null)
            {
                return;
            }

            var descriptors = UnityBridgeRuntime.CommandDescriptors;
            var commandCount = descriptors == null ? 0 : descriptors.Count;
            if (commandCount <= 0 || commandCount == m_lastCatalogPolicySummaryCommandCount)
            {
                return;
            }

            if (m_lastCatalogPolicyContext == null)
            {
                return;
            }

            var text = BuildCatalogPolicySummaryText(out var state, m_lastGatewayVirtualCommandCount, m_lastCatalogPolicyContext);
            SetChecklist(m_checklistCatalogPolicySummary, state, text);
        }

        string CatalogPolicySummaryText(out string checklistState)
        {
            return BuildCatalogPolicySummaryText(out checklistState, GatewayVirtualCommandCount(), ReadPolicyPreviewContext());
        }

        string BuildCatalogPolicySummaryText(out string checklistState, int gatewayVirtualCommandCount, PolicyPreviewContext policyContext)
        {
            var descriptors = UnityBridgeRuntime.CommandDescriptors;
            var unityBridgeCommandCount = descriptors == null ? 0 : descriptors.Count;
            m_lastCatalogPolicySummaryCommandCount = unityBridgeCommandCount;
            m_lastGatewayVirtualCommandCount = gatewayVirtualCommandCount;
            m_lastCatalogPolicyContext = policyContext;
            if (unityBridgeCommandCount == 0)
            {
                checklistState = "loading";
                return "Catalog policy summary: Unity Bridge catalog is not loaded yet (0 Unity Bridge + " + gatewayVirtualCommandCount + " Gateway virtual). Click H* Refresh after bridge startup completes.";
            }

            var ask = 0;
            var deny = 0;
            var allow = 0;
            var unknown = 0;
            var featureHidden = 0;
            var assignedPermissions = 0;
            for (var index = 0; index < unityBridgeCommandCount; index++)
            {
                var descriptor = descriptors[index];
                var featureStatus = FeatureFlagStatus(descriptor.FeatureFlag, policyContext);
                var decision = PolicyDecisionSummary(descriptor, policyContext);
                if (PolicySectionContains(policyContext.ProjectPolicySource, "deny", descriptor.Id)
                    || PolicySectionContains(policyContext.ProjectPolicySource, "allow", descriptor.Id)
                    || PolicySectionContains(policyContext.ProjectPolicySource, "ask", descriptor.Id))
                {
                    assignedPermissions++;
                }

                if (featureStatus.StartsWith("disabled:", StringComparison.Ordinal) || featureStatus.StartsWith("missing:", StringComparison.Ordinal))
                {
                    featureHidden++;
                }

                if (decision.StartsWith("ask:", StringComparison.Ordinal) || decision.StartsWith("default ask", StringComparison.Ordinal))
                {
                    ask++;
                }
                else if (decision.StartsWith("deny:", StringComparison.Ordinal) || decision.StartsWith("default deny", StringComparison.Ordinal))
                {
                    deny++;
                }
                else if (decision.StartsWith("allow:", StringComparison.Ordinal) || decision.StartsWith("default allow", StringComparison.Ordinal))
                {
                    allow++;
                }
                else
                {
                    unknown++;
                }
            }

            checklistState = assignedPermissions == 0 ? "error" : assignedPermissions == unityBridgeCommandCount ? "ok" : "info";
            var commandCountExpression = CommandCountExpression(unityBridgeCommandCount, gatewayVirtualCommandCount);
            return "Catalog policy summary: " + commandCountExpression
                + " commands (" + unityBridgeCommandCount + " Unity Bridge + " + gatewayVirtualCommandCount + " Gateway virtual)"
                + ", assigned permissions: <color=#a99be6>" + assignedPermissions + "/" + unityBridgeCommandCount + "</color>"
                + ", Unity Bridge policy: <color=#006397>" + allow + "</color> Allow/default allow, <color=#f2ab06>" + ask + "</color> Ask/default ask, <color=#ed0000>" + deny + "</color> Deny/default deny, " + unknown + " unknown"
                + ", feature-hidden: <color=#a99be6>" + featureHidden + "</color>. Refresh after Gateway/runtime update.";
        }

        int GatewayVirtualCommandCount()
        {
            var gatewayStatus = GatewayInstaller.GetStatus();
            return gatewayStatus.PackageGatewayVirtualCommandCount > 0
                ? gatewayStatus.PackageGatewayVirtualCommandCount
                : gatewayStatus.InstalledGatewayVirtualCommandCount;
        }

        string CommandCountExpression(int unityBridgeCommandCount, int gatewayVirtualCommandCount)
        {
            if (unityBridgeCommandCount == 0 && gatewayVirtualCommandCount == 0)
            {
                return "0";
            }

            return unityBridgeCommandCount + "+" + gatewayVirtualCommandCount;
        }

        GatewayProcessManager.GatewayRuntimeStatus ChecklistProcessStatus(GatewayProcessManager.GatewayRuntimeStatus processStatusOverride)
        {
            var processStatus = processStatusOverride ?? m_lastProcessStatus ?? GatewayProcessManager.GetStatus(false);
            if (!processStatus.HealthProbed && m_lastProbedProcessStatus != null)
            {
                return m_lastProbedProcessStatus;
            }

            return processStatus;
        }

        void RefreshHeaderShortcuts(GatewayInstaller.GatewayInstallStatus gatewayStatusOverride = null, GatewayProcessManager.GatewayRuntimeStatus processStatusOverride = null, GatewayPolicyManager.PolicyStatus policyStatusOverride = null)
        {
            if (m_headerShortcutBar == null)
            {
                return;
            }

            var gatewayStatus = gatewayStatusOverride ?? GatewayInstaller.GetStatus();
            var nodeStatus = NodeRuntimeInstaller.GetStatus();
            var processStatus = ChecklistProcessStatus(processStatusOverride);
            var policyStatus = policyStatusOverride ?? GatewayPolicyManager.GetStatus();
            var runtimeHealth = McpRuntimeHealthViewState.Evaluate(processStatus, gatewayStatus, LocalBridgeHeartbeatStale());
            var runtimeDowngrade = GatewayRuntimeDowngradeAvailable(gatewayStatus);
            if (m_updateRuntimeShortcutButton != null)
            {
                m_updateRuntimeShortcutButton.text = runtimeDowngrade ? "R↓" : "R↑";
                if (runtimeDowngrade) m_updateRuntimeShortcutButton.AddToClassList("mcp-danger-button");
                else m_updateRuntimeShortcutButton.RemoveFromClassList("mcp-danger-button");
            }
            var anyNeeded = false;
            anyNeeded |= SetHeaderShortcut(m_installPortableNodeShortcutButton, !nodeStatus.IsPortable, "Install Portable Node before installing or updating the Gateway runtime.");
            anyNeeded |= SetHeaderShortcut(m_installRuntimeShortcutButton, nodeStatus.IsPortable && GatewayRuntimeInstallShortcutNeeded(gatewayStatus), "Install the bundled Gateway runtime in LocalAppData.");
            anyNeeded |= SetHeaderShortcut(m_updateRuntimeShortcutButton, nodeStatus.IsPortable && GatewayRuntimeUpdateShortcutNeeded(gatewayStatus), runtimeDowngrade ? "Downgrade the LocalAppData Gateway runtime to this Unity package version after an explicit warning." : "Update the LocalAppData Gateway runtime from this Unity package.");
            anyNeeded |= SetHeaderShortcut(m_addCanonicalPathRegistryShortcutButton, CanonicalPathRegistryActionNeeded(), "Add the com.romanilyin scoped registry required before installing CanonicalPath from npmjs.");
            var canonicalPathState = ReadCanonicalPathDependencyState();
            if (m_installCanonicalPathShortcutButton != null)
            {
                m_installCanonicalPathShortcutButton.text = canonicalPathState.IsUpdate ? "CP↑" : "CP+";
                m_installCanonicalPathShortcutButton.SetEnabled(canonicalPathState.CanApply);
            }
            anyNeeded |= SetHeaderShortcut(m_installCanonicalPathShortcutButton, canonicalPathState.CanApply, CanonicalPathDependencyStateText(canonicalPathState));
            anyNeeded |= SetHeaderShortcut(m_startGatewayShortcutButton, runtimeHealth.StartOrRestartActionNeeded, "Start the installed Gateway, or explicitly stop and restart the existing Gateway. Restart interrupts current connections; no separate Stop is required.");
            anyNeeded |= SetHeaderShortcut(m_refreshHealthShortcutButton, runtimeHealth.HealthRefreshActionNeeded || CatalogRefreshActionNeeded(), "Refresh Gateway health, heartbeat and command catalog status.");
            anyNeeded |= SetHeaderShortcut(m_addPermissionsShortcutButton, PermissionsBaselineActionNeeded(policyStatus), "Apply the Readonly profile to add a project policy entry.");
            anyNeeded |= SetHeaderShortcut(m_allowAllShortcutButton, PermissionsAllowAllCompletionNeeded(policyStatus), "Complete the dangerous all-features plus Allow All configuration.");
            m_headerShortcutBar.style.display = anyNeeded ? DisplayStyle.Flex : DisplayStyle.None;
        }

        bool SetHeaderShortcut(Button button, bool needed, string tooltip)
        {
            if (button == null)
            {
                return false;
            }

            button.style.display = needed ? DisplayStyle.Flex : DisplayStyle.None;
            SetActionNeeded(button, needed, tooltip);
            return needed;
        }

        string GatewayRuntimeStatusText(GatewayInstaller.GatewayInstallStatus gatewayStatus, GatewayProcessManager.GatewayRuntimeStatus processStatus)
        {
            var runtimeHealth = McpRuntimeHealthViewState.Evaluate(processStatus, gatewayStatus);
            if (!gatewayStatus.CanStart)
            {
                return gatewayStatus.Message;
            }

            if (runtimeHealth.VersionMismatch)
            {
                return "Installed runtime " + DisplayVersion(gatewayStatus.InstalledRuntimeVersion) + " is ready, but running gateway is " + DisplayVersion(processStatus.HealthVersion) + ". Restart Gateway " + RuntimeVersionTransition(processStatus.HealthVersion, gatewayStatus.InstalledRuntimeVersion) + ".";
            }

            if (processStatus.HealthOk && processStatus.InvalidProjectRecordsCount > 0)
            {
                return gatewayStatus.Message + " Running version: " + processStatus.HealthVersion + ". Ignored heartbeat records: " + processStatus.InvalidProjectRecordsCount + ".";
            }

            if (processStatus.HealthOk && !string.IsNullOrEmpty(processStatus.HealthVersion))
            {
                return gatewayStatus.Message + " Running version: " + processStatus.HealthVersion + ".";
            }

            return gatewayStatus.Message + " Installed version: " + DisplayVersion(gatewayStatus.InstalledRuntimeVersion) + ".";
        }

        bool GatewayRuntimeIsCurrent(GatewayInstaller.GatewayInstallStatus gatewayStatus, GatewayProcessManager.GatewayRuntimeStatus processStatus)
        {
            return gatewayStatus.CanStart && !McpRuntimeHealthViewState.Evaluate(processStatus, gatewayStatus).ProcessNeedsRestart;
        }

        bool GatewayRuntimeNeedsAttention(GatewayInstaller.GatewayInstallStatus gatewayStatus, GatewayProcessManager.GatewayRuntimeStatus processStatus)
        {
            return gatewayStatus.CanStart ? McpRuntimeHealthViewState.Evaluate(processStatus, gatewayStatus).ProcessNeedsRestart : GatewayRuntimePackageAvailable(gatewayStatus) || gatewayStatus.HasBundledCli;
        }

        bool GatewayRuntimeInstallActionNeeded(GatewayInstaller.GatewayInstallStatus gatewayStatus)
        {
            return GatewayRuntimeInstallShortcutNeeded(gatewayStatus) || GatewayRuntimeUpdateShortcutNeeded(gatewayStatus);
        }

        bool GatewayRuntimeInstallShortcutNeeded(GatewayInstaller.GatewayInstallStatus gatewayStatus)
        {
            return GatewayRuntimePackageAvailable(gatewayStatus) && !gatewayStatus.HasBundledCli;
        }

        bool GatewayRuntimeUpdateShortcutNeeded(GatewayInstaller.GatewayInstallStatus gatewayStatus)
        {
            return GatewayRuntimePackageAvailable(gatewayStatus) && gatewayStatus.HasBundledCli && !gatewayStatus.RuntimeFresh;
        }

        bool GatewayRuntimeDowngradeAvailable(GatewayInstaller.GatewayInstallStatus gatewayStatus)
        {
            return GatewayRuntimeUpdateShortcutNeeded(gatewayStatus)
                && GatewayInstaller.TryCompareInstalledAndPackageVersions(gatewayStatus, out var comparison)
                && comparison > 0;
        }

        bool GatewayRuntimePackageAvailable(GatewayInstaller.GatewayInstallStatus gatewayStatus)
        {
            return gatewayStatus != null
                && !string.IsNullOrEmpty(gatewayStatus.PackageRuntimeCliPath)
                && File.Exists(gatewayStatus.PackageRuntimeCliPath);
        }

        bool CanonicalPathRegistryActionNeeded()
        {
            return !IsCanonicalPathRegistryConfigured();
        }

        bool CanonicalPathInstallActionNeeded()
        {
            return ReadCanonicalPathDependencyState().CanApply;
        }

        bool CanonicalPathInstalled()
        {
            return !string.IsNullOrEmpty(RegisteredPackageSummary(CanonicalPathPackageId));
        }

        bool CanonicalPathInstallInProgress()
        {
            return m_canonicalPathInstallRequest != null && !m_canonicalPathInstallRequest.IsCompleted;
        }

        string GatewayStartOrRestartTooltip(GatewayInstaller.GatewayInstallStatus gatewayStatus, bool processNeedsRestart)
        {
            if (processNeedsRestart)
            {
                return "Restart Gateway to use the installed runtime and current heartbeat schema.";
            }

            return gatewayStatus.CanStart ? "Start Gateway, then refresh health." : string.Empty;
        }

        bool LocalBridgeHeartbeatStale()
        {
            var heartbeatPath = UnityBridgeRuntime.HeartbeatFilePath;
            if (string.IsNullOrEmpty(heartbeatPath) || !File.Exists(heartbeatPath))
            {
                return false;
            }

            return (DateTime.UtcNow - File.GetLastWriteTimeUtc(heartbeatPath)).TotalSeconds > HeartbeatStaleWarningSeconds;
        }

        string GatewayRuntimeVersionSummary(GatewayInstaller.GatewayInstallStatus gatewayStatus, GatewayProcessManager.GatewayRuntimeStatus processStatus)
        {
            var runtimeHealth = McpRuntimeHealthViewState.Evaluate(processStatus, gatewayStatus);
            var summary = "installed=" + DisplayVersion(gatewayStatus.InstalledRuntimeVersion);
            if (processStatus.HealthOk && !string.IsNullOrEmpty(processStatus.HealthVersion))
            {
                summary += ", running=" + processStatus.HealthVersion;
                if (runtimeHealth.VersionMismatch)
                {
                    summary += " (restart required: " + RuntimeVersionTransition(processStatus.HealthVersion, gatewayStatus.InstalledRuntimeVersion) + ")";
                }
            }
            else
            {
                summary += ", running=not probed";
            }

            return summary;
        }

        void UpdateGatewayActionButtons(GatewayInstaller.GatewayInstallStatus gatewayStatus, GatewayProcessManager.GatewayRuntimeStatus processStatus)
        {
            var runtimeHealth = McpRuntimeHealthViewState.Evaluate(processStatus, gatewayStatus);
            if (m_startGatewayButton != null)
            {
                if (runtimeHealth.VersionMismatch)
                {
                    m_startGatewayButton.text = "Restart Gateway " + RuntimeVersionTransition(processStatus.HealthVersion, gatewayStatus.InstalledRuntimeVersion);
                }
                else if (gatewayStatus.CanStart)
                {
                    m_startGatewayButton.text = "Start Gateway " + DisplayVersion(gatewayStatus.InstalledRuntimeVersion);
                }
                else
                {
                    m_startGatewayButton.text = "Start Gateway";
                }
            }

            if (m_installRuntimeButton != null)
            {
                var runtimeDowngrade = GatewayRuntimeDowngradeAvailable(gatewayStatus);
                m_installRuntimeButton.text = gatewayStatus.RuntimeFresh
                    ? "Install Runtime"
                    : (runtimeDowngrade ? "Downgrade Runtime " : "Install Runtime ") + RuntimeVersionTransition(gatewayStatus.InstalledRuntimeVersion, gatewayStatus.PackageRuntimeVersion);
                if (runtimeDowngrade) m_installRuntimeButton.AddToClassList("mcp-danger-button");
                else m_installRuntimeButton.RemoveFromClassList("mcp-danger-button");
            }

            SetActionNeeded(m_installRuntimeButton, GatewayRuntimeInstallActionNeeded(gatewayStatus), "Install the bundled Gateway runtime version from this Unity package.");
            SetActionNeeded(m_startGatewayButton, runtimeHealth.StartOrRestartActionNeeded, GatewayStartOrRestartTooltip(gatewayStatus, runtimeHealth.ProcessNeedsRestart));
            var restartNeeded = runtimeHealth.ProcessNeedsRestart;
            for (var index = 0; index < m_restartGatewayButtons.Count; index++)
            {
                SetActionNeeded(m_restartGatewayButtons[index], restartNeeded, "Restart Gateway to use the installed runtime and current heartbeat schema.");
            }
        }

        void UpdateRefreshHealthActionButtons(GatewayProcessManager.GatewayRuntimeStatus processStatus)
        {
            var refreshNeeded = McpRuntimeHealthViewState.Evaluate(processStatus, null, LocalBridgeHeartbeatStale()).HealthRefreshActionNeeded;
            SetActionNeeded(m_setupRefreshHealthButton, refreshNeeded, "Refresh Gateway health and heartbeat status.");
            SetActionNeeded(m_statusRefreshHealthButton, refreshNeeded, "Refresh Gateway health and heartbeat status.");
        }

        void RefreshHeartbeatHealthActions()
        {
            var processStatus = m_lastProbedProcessStatus ?? m_lastProcessStatus;
            if (processStatus == null)
            {
                return;
            }

            var healthRefreshNeeded = McpRuntimeHealthViewState.Evaluate(processStatus, null, LocalBridgeHeartbeatStale()).HealthRefreshActionNeeded;
            var shortcutNeeded = healthRefreshNeeded || CatalogRefreshActionNeeded();
            SetActionNeeded(m_setupRefreshHealthButton, healthRefreshNeeded, "Refresh Gateway health and heartbeat status.");
            SetActionNeeded(m_statusRefreshHealthButton, healthRefreshNeeded, "Refresh Gateway health and heartbeat status.");
            SetHeaderShortcut(m_refreshHealthShortcutButton, shortcutNeeded, "Refresh Gateway health, heartbeat and command catalog status.");
            if (m_headerShortcutBar != null)
            {
                m_headerShortcutBar.style.display = AnyHeaderShortcutVisible() ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        bool AnyHeaderShortcutVisible()
        {
            return HeaderShortcutVisible(m_installPortableNodeShortcutButton)
                || HeaderShortcutVisible(m_installRuntimeShortcutButton)
                || HeaderShortcutVisible(m_updateRuntimeShortcutButton)
                || HeaderShortcutVisible(m_addCanonicalPathRegistryShortcutButton)
                || HeaderShortcutVisible(m_installCanonicalPathShortcutButton)
                || HeaderShortcutVisible(m_startGatewayShortcutButton)
                || HeaderShortcutVisible(m_refreshHealthShortcutButton)
                || HeaderShortcutVisible(m_addPermissionsShortcutButton)
                || HeaderShortcutVisible(m_allowAllShortcutButton);
        }

        static bool HeaderShortcutVisible(Button button)
        {
            return button != null && button.style.display.value == DisplayStyle.Flex;
        }

        bool CatalogRefreshActionNeeded()
        {
            var descriptors = UnityBridgeRuntime.CommandDescriptors;
            var commandCount = descriptors == null ? 0 : descriptors.Count;
            return commandCount == 0 || commandCount != m_lastCatalogPolicySummaryCommandCount;
        }

        string GatewayProcessStatusText(GatewayProcessManager.GatewayRuntimeStatus processStatus, GatewayInstaller.GatewayInstallStatus gatewayStatus)
        {
            var runtimeHealth = McpRuntimeHealthViewState.Evaluate(processStatus, gatewayStatus);
            if (processStatus.HealthOk && runtimeHealth.VersionMismatch)
            {
                return "Gateway health endpoint is reachable, but running version " + DisplayVersion(processStatus.HealthVersion) + " differs from installed runtime " + DisplayVersion(gatewayStatus.InstalledRuntimeVersion) + ". Restart Gateway " + RuntimeVersionTransition(processStatus.HealthVersion, gatewayStatus.InstalledRuntimeVersion) + ".";
            }

            if (processStatus.HealthOk && processStatus.InvalidProjectRecordsCount > 0)
            {
                return "Gateway health endpoint is reachable. Ignored old or invalid heartbeat record(s): " + processStatus.InvalidProjectRecordsCount + ".";
            }

            if (processStatus.HealthOk && processStatus.PolicyDegraded)
            {
                return "Gateway is reachable, but runtime policy is degraded. Risky commands fail closed; permitted reads use last-known-good policy. Restore a valid policy and Refresh Health; do not restart or replay blocked commands automatically.";
            }

            if (processStatus.HealthOk && processStatus.PolicyMetadataMalformed)
            {
                return "Gateway is reachable, but malformed policy health metadata was rejected safely. Policy status is unavailable; check the running Gateway version and Refresh Health.";
            }

            if (processStatus.HealthOk && runtimeHealth.ActionableStaleProjects)
            {
                return "Gateway health endpoint is reachable, but reports " + processStatus.StaleProjectRecordsCount + " stale heartbeat record(s). Refresh Health after the Unity bridge writes a fresh heartbeat.";
            }

            return processStatus.Message;
        }

        string GatewayProcessChecklistState(GatewayProcessManager.GatewayRuntimeStatus processStatus, GatewayInstaller.GatewayInstallStatus gatewayStatus)
        {
            return McpRuntimeHealthViewState.Evaluate(processStatus, gatewayStatus).CurrentHealthyRuntime ? "ok" : "warn";
        }

        string GatewayProcessChecklistText(GatewayProcessManager.GatewayRuntimeStatus processStatus, GatewayInstaller.GatewayInstallStatus gatewayStatus)
        {
            var runtimeHealth = McpRuntimeHealthViewState.Evaluate(processStatus, gatewayStatus);
            if (processStatus.HealthOk && runtimeHealth.VersionMismatch)
            {
                return "Needs attention - Gateway process is running version " + DisplayVersion(processStatus.HealthVersion) + " but installed runtime is " + DisplayVersion(gatewayStatus.InstalledRuntimeVersion) + ". Press " + GatewayRestartButtonText(processStatus, gatewayStatus) + " to restart it on the active runtime.";
            }

            if (processStatus.HealthOk && processStatus.InvalidProjectRecordsCount > 0)
            {
                return "Gateway is healthy and ignored " + processStatus.InvalidProjectRecordsCount + " old or invalid heartbeat record(s).";
            }

            if (processStatus.HealthOk && processStatus.PolicyDegraded)
            {
                return "Needs attention - policy is degraded. Risky commands fail closed and permitted reads use last-known-good policy. Restore a valid policy, then Refresh Health. No policy-only restart and no automatic replay.";
            }

            if (processStatus.HealthOk && processStatus.PolicyMetadataMalformed)
            {
                return "Needs attention - policy metadata is malformed and safely unavailable. Check the running Gateway version, then Refresh Health.";
            }

            if (processStatus.HealthOk && runtimeHealth.ActionableStaleProjects)
            {
                return "Needs attention - Gateway health is reachable but reports " + processStatus.StaleProjectRecordsCount + " stale heartbeat record(s). Refresh Health after this Editor writes a fresh heartbeat.";
            }

            return ChecklistText(processStatus.HealthOk || processStatus.ProcessAlive, processStatus.ProcessAlive && !processStatus.HealthOk, processStatus.Message, "Start Gateway, then Refresh Health.");
        }

        string DisplayVersion(string value)
        {
            return string.IsNullOrEmpty(value) ? "unknown" : value;
        }

        string RuntimeVersionTransition(string fromVersion, string toVersion)
        {
            return DisplayVersion(fromVersion) + " -> " + DisplayVersion(toVersion);
        }

        string GatewayRestartButtonText(GatewayProcessManager.GatewayRuntimeStatus processStatus, GatewayInstaller.GatewayInstallStatus gatewayStatus)
        {
            return "Restart Gateway " + RuntimeVersionTransition(processStatus.HealthVersion, gatewayStatus.InstalledRuntimeVersion);
        }

        void SetChecklist(Label label, string state, string text)
        {
            if (label == null)
            {
                return;
            }

            label.text = McpWindowLocalization.Text(text);
            SetChecklistState(label, state);
        }

        void SetChecklistState(Label label, string state)
        {
            var field = label.parent;
            RemoveChecklistState(label);
            if (field != null)
            {
                RemoveChecklistState(field);
                field.AddToClassList("mcp-checklist--" + state);
            }

            var icon = field == null ? null : field.Q<Label>(className: "mcp-checklist-icon");
            if (icon != null)
            {
                icon.text = ChecklistIcon(state);
                icon.tooltip = ChecklistTooltip(state);
                RemoveChecklistState(icon);
                icon.AddToClassList("mcp-checklist-icon--" + state);
            }
        }

        void RemoveChecklistState(VisualElement element)
        {
            element.RemoveFromClassList("mcp-checklist--ok");
            element.RemoveFromClassList("mcp-checklist--warn");
            element.RemoveFromClassList("mcp-checklist--error");
            element.RemoveFromClassList("mcp-checklist--optional");
            element.RemoveFromClassList("mcp-checklist--info");
            element.RemoveFromClassList("mcp-checklist--loading");
            element.RemoveFromClassList("mcp-checklist-icon--ok");
            element.RemoveFromClassList("mcp-checklist-icon--warn");
            element.RemoveFromClassList("mcp-checklist-icon--error");
            element.RemoveFromClassList("mcp-checklist-icon--optional");
            element.RemoveFromClassList("mcp-checklist-icon--info");
            element.RemoveFromClassList("mcp-checklist-icon--loading");
        }

        string ChecklistIcon(string state)
        {
            switch (state)
            {
                case "ok":
                    return "✔";
                case "warn":
                    return "▲";
                case "info":
                    return "◇";
                case "loading":
                    return "◷";
                case "optional":
                    return "◇";
                default:
                    return "●";
            }
        }

        string ChecklistTooltip(string state)
        {
            switch (state)
            {
                case "ok":
                    return "Ready";
                case "warn":
                    return "Needs attention";
                case "info":
                    return "Partial";
                case "loading":
                    return "Loading";
                case "optional":
                    return "Optional";
                default:
                    return "Blocked or failed";
            }
        }

        string ChecklistText(bool ok, bool warn, string okOrWarnText, string nextAction)
        {
            if (ok)
            {
                return "Ready - " + okOrWarnText;
            }

            return (warn ? "Needs attention - " : "Next action - ") + nextAction;
        }

        void RefreshMcpTokenSetup()
        {
            if (m_setupMcpTokenStatus == null)
            {
                return;
            }

            m_setupMcpTokenEnv.text = McpTokenEnvName;
            m_setupMcpTokenStatus.text = McpTokenStatus();
            m_setupMcpClientHeader.text = "X-SGG-Unity-MCP-Token: ${" + McpTokenEnvName + "}";
        }

        void RefreshMcpClientConfig(GatewayNetworkModeManager.NetworkStatus statusOverride = null)
        {
            if (m_setupMcpUrl == null)
            {
                return;
            }

            var status = statusOverride ?? GatewayNetworkModeManager.GetStatus(CurrentConnectionMode());
            m_setupMcpUrl.text = status.McpUrl;
            m_setupHealthUrl.text = HealthUrl;
            m_setupProjectId.text = ProjectPaths.ProjectId;
        }

        GatewayNetworkModeManager.NetworkStatus RefreshMcpConnection()
        {
            if (m_mcpConnectionSelectedMode == null)
            {
                return null;
            }

            var mode = CurrentConnectionMode();
            var status = GatewayNetworkModeManager.GetStatus(mode);
            m_openCodePresentationNetworkStatus = status;
            if (m_mcpConnectionModeField != null)
            {
                var label = GatewayNetworkModeManager.LabelForMode(mode);
                if (m_mcpConnectionModeField.value != label)
                {
                    m_mcpConnectionModeField.SetValueWithoutNotify(label);
                }
            }

            m_mcpConnectionSelectedMode.text = GatewayNetworkModeManager.LabelForMode(mode);
            m_mcpConnectionGatewayBind.text = status.Host + ":" + status.Port + (status.AllowNonLoopback ? " (non-loopback allowed)" : " (loopback/default)");
            m_mcpConnectionMcpUrl.text = status.McpUrl;
            m_mcpConnectionWslUrl.text = mode == GatewayNetworkModeManager.ModeRemoteHttp ? status.McpUrl : "Windows stdio shim uses Windows loopback internally.";
            m_mcpConnectionTokenStatus.text = status.TokenEnv + " is " + (status.TokenPresent ? "Present" : "Missing");
            m_mcpConnectionSecurityStatus.text = status.Message;
            m_mcpConnectionConfigPath.text = status.ConfigPath;
            m_mcpConnectionOpenCodeWorkspaceRoot.SetValueWithoutNotify(status.OpenCodeWorkspaceRoot ?? string.Empty);
            m_mcpConnectionOpenCodePath.text = string.IsNullOrEmpty(status.OpenCodePath)
                ? status.OpenCodeConfigConflict ? status.OpenCodeJsoncPath + " | " + status.OpenCodeJsonPath : "Unavailable"
                : status.OpenCodePath;
            m_mcpConnectionOpenCodeStatus.text = status.OpenCodeSavedWorkspaceRejected
                ? McpWindowLocalization.Text("Warning - the saved OpenCode workspace was rejected. Automatic resolution is active; use Browse to replace it or Auto to reset it.") + " " + status.OpenCodeTargetMessage
                : status.OpenCodeTargetMessage;
            m_mcpConnectionReportedWorkspace.text = ReportedWorkspaceVerification(
                status,
                m_lastProcessStatus != null && m_lastProcessStatus.HealthProbed ? m_lastProcessStatus : null);
            return status;
        }

        internal static string ReportedWorkspaceVerification(
            GatewayNetworkModeManager.NetworkStatus networkStatus,
            GatewayProcessManager.GatewayRuntimeStatus processStatus)
        {
            if (processStatus == null || !processStatus.HealthProbed || !processStatus.HealthOk)
            {
                return McpWindowLocalization.Text("Unavailable - refresh Gateway health after OpenCode connects.");
            }
            if (!networkStatus.OpenCodeTargetAvailable || string.IsNullOrEmpty(networkStatus.OpenCodeWorkspaceHash))
            {
                return McpWindowLocalization.Text("Unavailable - select a valid OpenCode workspace first.");
            }
            if (processStatus.ClientRootsMetadataMalformed)
            {
                return McpWindowLocalization.Text("Unavailable - Gateway client roots evidence is malformed.");
            }
            if (!processStatus.ClientRootsMetadataAvailable)
            {
                return McpWindowLocalization.Text("Unavailable - Gateway has no client roots evidence yet.");
            }
            if (processStatus.ClientRootsObservationCount == 0)
            {
                return McpWindowLocalization.Text("Unavailable - Gateway has no client roots evidence yet.");
            }

            var expected = networkStatus.OpenCodeWorkspaceHash;
            var matched = Array.IndexOf(processStatus.ClientRootHashes, expected) >= 0;
            if (matched && processStatus.ClientRootHashes.Length == 1)
            {
                return McpWindowLocalization.Text("Verified - a connected MCP session reported the selected OpenCode workspace.");
            }
            if (matched)
            {
                return McpWindowLocalization.Text("Ambiguous - the selected workspace was reported together with other connected-session roots.");
            }
            if (processStatus.ClientRootHashes.Length > 0)
            {
                return McpWindowLocalization.Text("Mismatch - connected MCP sessions reported different workspace roots.");
            }
            return McpWindowLocalization.Text("Unavailable - connected clients did not provide a usable file workspace root.");
        }

        void RefreshOptionalToolsSetup()
        {
            if (m_setupRoslynStatus == null)
            {
                return;
            }

            m_setupRoslynStatus.text = RoslynStatusText();
            m_setupRoslynPath.text = EditorRoslynInstaller.ProjectRelativeFolder;
            m_setupRoslynFeature.text = "Dynamic C# compile/validation helpers; not required for read-only baseline.";
            m_setupRoslynPolicyRisk.text = "Execute-risk command family; keep disabled until explicitly needed and allowed by policy.";
            m_setupUnityAiSummary.text = "Optional baseline: " + UnityAiAssistantPackageStatus();
            RefreshOpenCodePresentationSetup();
            RefreshUnityCliPipelineSetup();
        }

        void RefreshSetupPreferences()
        {
            if (m_setupLanguageCurrent == null)
            {
                return;
            }

            m_setupLanguageCurrent.text = McpWindowLocalization.CurrentLanguageDisplayName();
            if (m_languageField != null)
            {
                var languageNames = McpWindowLocalization.LanguageDisplayNames();
                var languageIndex = McpWindowLocalization.LanguageIndex(McpWindowLocalization.CurrentLanguage);
                if (languageIndex >= 0 && languageIndex < languageNames.Count)
                {
                    m_languageField.SetValueWithoutNotify(languageNames[languageIndex]);
                }
            }
        }

        string RoslynStatusText()
        {
            return "installed=" + EditorRoslynInstaller.IsInstalled()
                + ", loaded=" + EditorRoslynInstaller.IsCompilerLoaded()
                + ", path=" + EditorRoslynInstaller.ProjectRelativeFolder;
        }

        string CanonicalPathRegistryStatusText()
        {
            var manifestPath = ProjectManifestPath();
            if (!File.Exists(manifestPath))
            {
                return "Missing Packages/manifest.json at " + manifestPath;
            }

            var source = File.ReadAllText(manifestPath);
            var hasRegistryUrl = TryFindCanonicalPathRegistryObject(source, out var objectStart, out var objectEnd);
            var hasScope = hasRegistryUrl
                ? RegistryObjectContainsScope(source, objectStart, objectEnd)
                : source.Contains(JsonUtil.String(CanonicalPathScope));
            if (hasRegistryUrl && hasScope)
            {
                return "Configured: " + CanonicalPathScope + " -> " + CanonicalPathRegistryUrl;
            }

            if (hasRegistryUrl)
            {
                return "Scope missing: add " + CanonicalPathScope + " to the npmjs scoped registry.";
            }

            if (hasScope)
            {
                return "Registry URL missing: set " + CanonicalPathRegistryUrl + " for " + CanonicalPathScope + ".";
            }

            return "Missing: configure " + CanonicalPathScope + " scoped registry for CanonicalPath.";
        }

        bool IsCanonicalPathRegistryConfigured()
        {
            var manifestPath = ProjectManifestPath();
            if (!File.Exists(manifestPath))
            {
                return false;
            }

            var source = File.ReadAllText(manifestPath);
            return TryFindCanonicalPathRegistryObject(source, out var objectStart, out var objectEnd)
                && RegistryObjectContainsScope(source, objectStart, objectEnd);
        }

        string CanonicalPathDependencyStatusText()
        {
            return CanonicalPathDependencyStateText(ReadCanonicalPathDependencyState());
        }

        CanonicalPathDependencyState ReadCanonicalPathDependencyState()
        {
            UnityEditor.PackageManager.PackageInfo installed = null;
            foreach (var package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            {
                if (string.Equals(package.name, CanonicalPathPackageId, StringComparison.Ordinal))
                {
                    installed = package;
                    break;
                }
            }
            var declared = string.Empty;
            var readable = false;
            try
            {
                var manifest = ProjectManifestPath();
                using (var stream = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    readable = TryReadCanonicalPathManifestDependency(stream, out declared);
                }
            }
            catch { readable = false; }
            return CanonicalPathDependencyState.Evaluate(installed != null, installed == null ? string.Empty : installed.version,
                installed == null ? string.Empty : installed.source.ToString(), declared, CanonicalPathVersion,
                CanonicalPathInstallInProgress(), CanonicalPathBridge.IsAvailable, readable);
        }

        internal const int CanonicalPathManifestMaxBytes = 1024 * 1024;

        internal static bool TryReadCanonicalPathManifestDependency(Stream stream, out string declared)
        {
            declared = string.Empty;
            if (stream == null || !stream.CanRead) return false;
            try
            {
                var bytes = new byte[CanonicalPathManifestMaxBytes + 1];
                var total = 0;
                while (total < bytes.Length)
                {
                    var count = stream.Read(bytes, total, bytes.Length - total);
                    if (count <= 0) break;
                    total += count;
                }
                if (total > CanonicalPathManifestMaxBytes) return false;
                var start = total >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
                var source = new UTF8Encoding(false, true).GetString(bytes, start, total - start);
                var path = new[] { "dependencies", CanonicalPathPackageId };
                if (!PipelineJsonEvidenceParser.TryReadOptionalStringPath(source, path, out var value)) return false;
                if (string.IsNullOrEmpty(value) && PipelineJsonEvidenceParser.TryReadStringPath(source, path, out _)) return false;
                declared = value;
                return true;
            }
            catch { return false; }
        }

        string CanonicalPathDependencyStateText(CanonicalPathDependencyState state)
        {
            var summary = McpWindowLocalization.Format("CanonicalPath: installed {0} ({1}); required {2}.",
                state.Registered ? state.InstalledVersion : "-", state.Source, state.RequiredVersion);
            string guidance;
            switch (state.Action)
            {
                case CanonicalPathDependencyAction.None: guidance = "Ready - CanonicalPath provider is available."; break;
                case CanonicalPathDependencyAction.Install: guidance = "Install CanonicalPath to enable bridge discovery and path-bound commands."; break;
                case CanonicalPathDependencyAction.Update: guidance = "Update CanonicalPath to the required version; the installed registry provider is older."; break;
                case CanonicalPathDependencyAction.Busy: guidance = "CanonicalPath package change is in progress; wait for completion."; break;
                case CanonicalPathDependencyAction.Resolving: guidance = "CanonicalPath is declared but not resolved to the expected package; wait for Package Manager or inspect resolution. Do not repeat the update."; break;
                case CanonicalPathDependencyAction.Reload: guidance = "CanonicalPath version is sufficient but its provider is unavailable; inspect reload and compilation diagnostics."; break;
                case CanonicalPathDependencyAction.Manual: guidance = "CanonicalPath needs manual package-source or manifest review; Setup will not change its source or downgrade it."; break;
                default: guidance = "CanonicalPath version or manifest evidence is unavailable; inspect Package Manager and the project manifest before changing packages."; break;
            }
            return summary + " " + McpWindowLocalization.Text(guidance);
        }

        string McpTokenStatus()
        {
            return FormatTokenPresence(Environment.GetEnvironmentVariable("SGG_UNITY_MCP_TOKEN"));
        }
    }
}
