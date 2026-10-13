using System;
using System.IO;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Setup;
using SGG.UnityMCP.Editor.UI.Localization;
using SGG.UnityMCP.Editor.UI.State;
using UnityEngine.UIElements;

namespace SGG.UnityMCP.Editor.UI
{
    public sealed partial class UnityMcpSetupWindow
    {
        Label m_ftueUnityBridge;
        Label m_ftuePortableNode;
        Label m_ftueGatewayRuntime;
        Label m_ftueGatewayProcess;
        Label m_ftueMcpToken;
        Label m_ftueClientConfig;
        Label m_ftuePermissions;
        Label m_ftueReadonlyPermissions;
        Label m_ftueAllowAllPermissions;
        Label m_ftueSmokeTest;
        Button m_ftueInstallNodeButton;
        Button m_ftueInstallRuntimeButton;
        Button m_ftueStartGatewayButton;
        Button m_ftueCreateClientConfigButton;
        Button m_ftueCreatePermissionsButton;
        Button m_ftueApplyReadonlyButton;
        McpFtueState m_lastFtueState;
        NodeRuntimeInstaller.NodeRuntimeStatus m_lastFtueNodeStatus;
        GatewayInstaller.GatewayInstallStatus m_lastFtueGatewayStatus;
        GatewayPolicyManager.PolicyStatus m_lastFtuePolicyStatus;
        bool m_ftueVisibilityInitialized;
        bool m_ftueWasRequired;

        void BuildFtuePanel()
        {
            HelpRow(m_ftuePanel, "First-time setup follows one dependency-ordered path. Each step explains why it is needed and reuses the same action available on Setup or Permissions.");

            var bridgeActions = AddFtueStep("1. Unity bridge", "required", "The Gateway discovers this open Unity project through its local heartbeat and command catalog.", out m_ftueUnityBridge);
            AddButton(bridgeActions, "Open Setup Details", () => SelectTab("Setup"));

            var nodeActions = AddFtueStep("2. Portable Node", "required", "Installs an isolated Node runtime for the Gateway without changing the system PATH.", out m_ftuePortableNode);
            m_ftueInstallNodeButton = AddButton(nodeActions, "Install Portable Node", () => RunAction("Install Portable Node", () => NodeRuntimeInstaller.InstallPortableNode(true)));
            m_ftueInstallNodeButton.name = "ftue-install-portable-node";

            var runtimeActions = AddFtueStep("3. Gateway Runtime", "required", "Installs the bundled Gateway code in LocalAppData after Portable Node is ready.", out m_ftueGatewayRuntime);
            m_ftueInstallRuntimeButton = AddButton(runtimeActions, "Install Gateway Runtime", () => RunAction("Install Gateway Runtime", InstallGatewayRuntime, true));
            m_ftueInstallRuntimeButton.name = "ftue-install-runtime";

            var processActions = AddFtueStep("4. Gateway Process", "required", "Starts the local MCP endpoint or restarts it when the installed runtime/config changed.", out m_ftueGatewayProcess);
            m_ftueStartGatewayButton = AddButton(processActions, "Start or Restart Gateway", StartOrRestartGateway);
            m_ftueStartGatewayButton.name = "ftue-start-gateway";
            AddButton(processActions, "Refresh Health", RefreshHealthAndChecklist);

            var tokenActions = AddFtueStep("5. MCP Token", "conditional required", "Remote HTTP requires token authentication. Windows stdio/loopback can proceed without a token, but a session token remains recommended.", out m_ftueMcpToken);
            AddButton(tokenActions, "Generate Token in Setup", () =>
            {
                GenerateSessionToken();
                SelectTab("Setup");
            });
            AddButton(tokenActions, "Open Token Setup", () => SelectTab("Setup"));

            var clientActions = AddFtueStep("6. MCP Client Config", "required", "Creates the selected OpenCode v2 client flow for the single local Gateway endpoint. Config layers and the actual client connection still need a smoke test.", out m_ftueClientConfig);
            m_ftueCreateClientConfigButton = AddButton(clientActions, "Create/Update OpenCode Config", CreateOrUpdateOpenCodeConfig);
            m_ftueCreateClientConfigButton.name = "ftue-create-client-config";
            AddButton(clientActions, "Open Connection Setup", () => SelectTab("Setup"));

            var permissionsActions = AddFtueStep("7. Permissions", "required", "Creates user config and a project-scoped policy foundation. Permissions and feature flags remain separate controls.", out m_ftuePermissions);
            m_ftueCreatePermissionsButton = AddButton(permissionsActions, "Create Config/Policy Files", () => RunAction("Create Config/Policy Files", GatewayPolicyManager.EnsureUserConfig));
            m_ftueCreatePermissionsButton.name = "ftue-create-permissions";
            AddButton(permissionsActions, "Open Permissions", () => SelectTab("Permissions"));

            var readonlyActions = AddFtueStep("8. Readonly permissions", "recommended", "Recommended first profile for discovery, diagnostics and a read-only smoke test. Another explicit non-empty project profile also satisfies onboarding.", out m_ftueReadonlyPermissions);
            m_ftueApplyReadonlyButton = AddButton(readonlyActions, "Apply Readonly", () => ConfirmApplyProfile(GatewayPolicyManager.ReadonlyProfile, "Readonly"));
            m_ftueApplyReadonlyButton.name = "ftue-apply-readonly";
            AddButton(readonlyActions, "Review Profiles", () => SelectTab("Permissions"));

            var allowAllActions = AddFtueStep("9. Allow All permissions", "optional", "Dangerous alternative: removes ask gates for built-in/trusted Gateway-visible commands. It is never applied automatically or required for onboarding.", out m_ftueAllowAllPermissions);
            var allowAllButton = AddButton(allowAllActions, "Apply Allow All", () => ConfirmAllowAll(false));
            allowAllButton.name = "ftue-apply-allow-all";
            allowAllButton.AddToClassList("mcp-danger-button");

            var smokeActions = AddFtueStep("10. First MCP Smoke Test", "required", "From the configured MCP client, discover projects and commands, then call compile.status. Completion comes from a successful Bridge invoke, not from copying text.", out m_ftueSmokeTest);
            var copySmokeButton = AddButton(smokeActions, "Copy Smoke Test Calls", CopySmokeTestCalls);
            copySmokeButton.name = "ftue-copy-smoke-test";
            AddButton(smokeActions, "Recheck", () => RefreshAll(true));
        }

        VisualElement AddFtueStep(string title, string importance, string explanation, out Label status)
        {
            var section = AddSection(m_ftuePanel, title);
            status = AddChecklistRow(section, "Status", importance, "Checking current setup state.");
            HelpRow(section, explanation);
            return AddActions(section);
        }

        void RefreshFtue(GatewayProcessManager.GatewayRuntimeStatus processStatus, GatewayNetworkModeManager.NetworkStatus networkStatus)
        {
            if (m_ftuePanel == null || m_lastFtueNodeStatus == null || m_lastFtueGatewayStatus == null || m_lastFtuePolicyStatus == null || networkStatus == null)
            {
                return;
            }

            var rules = ReadProjectPolicyRules(m_lastFtuePolicyStatus.PolicyPath, m_lastFtuePolicyStatus.ProjectId);
            var rulesFingerprint = McpFtueProfileTracker.RulesFingerprint(rules.Allow, rules.Ask, rules.Deny);
            var profileReady = rules.Allow.Count + rules.Ask.Count + rules.Deny.Count > 0
                && McpFtueProfileTracker.HasCurrentExplicitProfile(ProjectPaths.ProjectRoot, rulesFingerprint);
            var allowAll = rules.Allow.Contains("*") && rules.Ask.Count == 0 && rules.Deny.Count == 0;
            var tokenRequired = !networkStatus.LoopbackBind;
            var bridgeReady = UnityBridgeRuntime.BridgePort > 0
                && File.Exists(UnityBridgeRuntime.HeartbeatFilePath)
                && !LocalBridgeHeartbeatStale();
            var runtimeHealth = McpRuntimeHealthViewState.Evaluate(processStatus, m_lastFtueGatewayStatus, LocalBridgeHeartbeatStale());
            var state = new McpFtueState
            {
                UnityBridgeReady = bridgeReady,
                PortableNodeReady = m_lastFtueNodeStatus.IsAvailable && m_lastFtueNodeStatus.IsPortable,
                GatewayRuntimeReady = m_lastFtueGatewayStatus.CanStart && m_lastFtueGatewayStatus.RuntimeFresh,
                GatewayProcessReady = runtimeHealth.CurrentHealthyRuntime,
                TokenContractReady = networkStatus.BindSecurityReady,
                ClientConfigReady = networkStatus.OpenCodeConfigReady,
                PermissionsReady = m_lastFtuePolicyStatus.UserConfigReady && m_lastFtuePolicyStatus.ProjectPolicyReady,
                PermissionProfileReady = profileReady,
                SmokeTestReady = McpFtueSmokeTracker.HasSuccessfulCompileStatus(ProjectPaths.ProjectRoot),
            };
            m_lastFtueState = state;

            SetFtueChecklist(m_ftueUnityBridge, state.UnityBridgeReady ? "ok" : "error", state.UnityBridgeReady ? "Ready - current Unity project heartbeat is active and discoverable." : "Next action - keep this project open, finish compilation, then recheck Setup details.");
            SetFtueChecklist(m_ftuePortableNode, state.PortableNodeReady ? "ok" : "error", state.PortableNodeReady ? "Ready - isolated Portable Node is available." : "Next action - install Portable Node before the Gateway runtime.");
            SetFtueChecklist(m_ftueGatewayRuntime, state.GatewayRuntimeReady ? "ok" : "error", state.GatewayRuntimeReady ? "Ready - installed Gateway runtime matches this package." : "Next action - install or update the bundled Gateway runtime.");
            SetFtueChecklist(m_ftueGatewayProcess, state.GatewayProcessReady ? "ok" : "error", state.GatewayProcessReady ? "Ready - Gateway health and runtime version are current." : "Next action - start/restart Gateway, then refresh health.");
            SetFtueChecklist(m_ftueMcpToken, state.TokenContractReady ? (tokenRequired ? "ok" : "info") : "error", state.TokenContractReady ? tokenRequired ? "Ready - Remote HTTP token is present." : "Ready - token is optional for the selected Windows stdio/loopback flow." : "Next action - configure a token before using Remote HTTP.");
            SetFtueChecklist(m_ftueClientConfig, state.ClientConfigReady ? "ok" : "error", state.ClientConfigReady
                ? "Ready - the selected file contains the OpenCode v2 SGG Unity MCP flow. Ancestor/global/inline config and the client connection are not verified."
                : networkStatus.OpenCodeConfigConflict
                    ? "Next action - resolve the OpenCode config/layer conflict manually. " + networkStatus.OpenCodeTargetMessage
                    : "Next action - create or update the selected OpenCode config for the selected connection mode.");
            SetFtueChecklist(m_ftuePermissions, state.PermissionsReady ? "ok" : "error", state.PermissionsReady ? "Ready - config and project policy entry are present." : "Next action - create config/policy files and review this Project Id in Permissions.");
            SetFtueChecklist(m_ftueReadonlyPermissions, profileReady && !allowAll ? "ok" : profileReady ? "optional" : "warn", profileReady && !allowAll ? "Ready - an explicit non-Allow-All project profile is active." : profileReady ? "Optional - Allow All is active; Readonly remains the safer first profile." : "Next action - apply Readonly or explicitly choose another valid profile.");
            SetFtueChecklist(m_ftueAllowAllPermissions, allowAll && profileReady ? "ok" : "optional", allowAll && profileReady ? "Ready - Allow All is active by explicit choice." : "Optional - not required when Readonly or another valid profile is active.");
            SetFtueChecklist(m_ftueSmokeTest, state.SmokeTestReady ? "ok" : "warn", state.SmokeTestReady ? "Ready - this project received a successful compile.status Bridge invoke." : "Next action - run the copied discovery sequence from the MCP client, then recheck.");

            var runtimeDowngrade = GatewayRuntimeDowngradeAvailable(m_lastFtueGatewayStatus);
            m_ftueInstallRuntimeButton.text = runtimeDowngrade ? "Downgrade Gateway Runtime" : "Install Gateway Runtime";
            if (runtimeDowngrade) m_ftueInstallRuntimeButton.AddToClassList("mcp-danger-button");
            else m_ftueInstallRuntimeButton.RemoveFromClassList("mcp-danger-button");
            m_ftueInstallRuntimeButton.SetEnabled(state.PortableNodeReady);
            m_ftueStartGatewayButton.SetEnabled(state.GatewayRuntimeReady);
            m_ftueCreateClientConfigButton.SetEnabled(state.GatewayRuntimeReady && state.TokenContractReady && networkStatus.OpenCodeTargetAvailable && !networkStatus.OpenCodeConfigConflict);
            m_ftueCreatePermissionsButton.SetEnabled(state.GatewayRuntimeReady);
            m_ftueApplyReadonlyButton.SetEnabled(state.PermissionsReady && state.PortableNodeReady);
            ApplyFtueVisibility(!state.IsComplete);
        }

        void RefreshFtueAfterHeartbeat()
        {
            if (m_lastFtueState == null)
            {
                return;
            }

            m_lastFtueState.UnityBridgeReady = UnityBridgeRuntime.BridgePort > 0
                && File.Exists(UnityBridgeRuntime.HeartbeatFilePath)
                && !LocalBridgeHeartbeatStale();
            m_lastFtueState.SmokeTestReady = McpFtueSmokeTracker.HasSuccessfulCompileStatus(ProjectPaths.ProjectRoot);
            SetFtueChecklist(m_ftueUnityBridge, m_lastFtueState.UnityBridgeReady ? "ok" : "error", m_lastFtueState.UnityBridgeReady ? "Ready - current Unity project heartbeat is active and discoverable." : "Next action - keep this project open, finish compilation, then recheck Setup details.");
            SetFtueChecklist(m_ftueSmokeTest, m_lastFtueState.SmokeTestReady ? "ok" : "warn", m_lastFtueState.SmokeTestReady ? "Ready - this project received a successful compile.status Bridge invoke." : "Next action - run the copied discovery sequence from the MCP client, then recheck.");
            ApplyFtueVisibility(!m_lastFtueState.IsComplete);
        }

        void SetFtueChecklist(Label label, string state, string text)
        {
            SetChecklist(label, state, McpWindowLocalization.Text(text));
        }

        void ApplyFtueVisibility(bool required)
        {
            if (m_ftueTab == null || m_ftuePanel == null)
            {
                return;
            }

            m_ftueTab.style.display = required ? DisplayStyle.Flex : DisplayStyle.None;
            var appeared = required && (!m_ftueVisibilityInitialized || !m_ftueWasRequired);
            if (appeared)
            {
                SelectTab("FTUE");
            }
            else if (!required && string.Equals(WindowState.SelectedTab, "FTUE", StringComparison.Ordinal))
            {
                SelectTab("Setup");
            }

            m_ftueWasRequired = required;
            m_ftueVisibilityInitialized = true;
        }

        internal void ApplyFtueStateForCommand(McpFtueState state)
        {
            m_lastFtueState = state;
            ApplyFtueVisibility(state == null || !state.IsComplete);
        }

        internal bool FtueTabVisibleForCommand()
        {
            return m_ftueTab != null && m_ftueTab.style.display.value == DisplayStyle.Flex;
        }

        internal bool FtueCompleteForCommand()
        {
            return m_lastFtueState != null && m_lastFtueState.IsComplete;
        }

        internal bool FtueSmokeTestReadyForCommand()
        {
            return m_lastFtueState != null && m_lastFtueState.SmokeTestReady;
        }

        internal bool FtueClientConfigReadyForCommand()
        {
            return m_lastFtueState != null && m_lastFtueState.ClientConfigReady;
        }

        internal bool FtuePermissionProfileReadyForCommand()
        {
            return m_lastFtueState != null && m_lastFtueState.PermissionProfileReady;
        }
    }
}
