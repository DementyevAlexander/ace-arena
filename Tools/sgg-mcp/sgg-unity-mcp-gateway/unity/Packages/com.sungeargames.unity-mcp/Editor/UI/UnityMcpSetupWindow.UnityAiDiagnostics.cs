using System;
using System.IO;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Setup;
using SGG.UnityMCP.Editor.UI.State;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace SGG.UnityMCP.Editor.UI
{
    public sealed partial class UnityMcpSetupWindow
    {
        UnityEditor.PackageManager.Requests.AddRequest m_unityAiAssistantInstallRequest;
        Label m_unityAiAssistantBaseline;
        Label m_unityAiAssistantWorksWithoutOfficialPackage;
        Label m_unityAiAssistantAddsWhenOfficialPackageInstalled;
        Label m_unityAiAssistantPackageStatus;
        Label m_unityAiAssistantRelayStatus;
        Label m_unityAiAssistantSkillsStatus;
        Label m_unityAiAssistantDiscoverySources;
        Label m_unityAiAssistantImportSemantics;
        Label m_unityAiAssistantSupportedTools;
        Label m_unityAiAssistantPartialTools;
        Label m_unityAiAssistantDeferredTools;
        Label m_diagnosticsRuntimePath;
        Label m_diagnosticsConfigPath;
        Label m_diagnosticsPolicyPath;
        Label m_diagnosticsLogPath;
        Label m_diagnosticsHeartbeatPath;
        Label m_diagnosticsProblemReportsPath;
        Label m_diagnosticsBridgeToken;
        Label m_diagnosticsMcpToken;
        Label m_diagnosticsCanonicalPath;
        Label m_diagnosticsPathGuard;
        Label m_diagnosticsFeatureFlags;
        Label m_diagnosticsAuditStatus;
        Label m_diagnosticsAvailability;
        Label m_diagnosticsWindowTiming;
        Label m_diagnosticsRedaction;
        Label m_diagnosticsLastErrors;

        string UnityAiAssistantPackageStatus()
        {
            var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
            for (var index = 0; index < packages.Length; index++)
            {
                if (string.Equals(packages[index].name, UnityAiAssistantPackageId, StringComparison.Ordinal))
                {
                    return "Installed: " + packages[index].version + " from " + packages[index].source + ". Optional for SGGMcp local parity.";
                }
            }

            var manifestVersion = ReadProjectManifestDependencyVersion(UnityAiAssistantPackageId);
            if (!string.IsNullOrEmpty(manifestVersion))
            {
                return "Declared in Packages/manifest.json: " + manifestVersion + ". Unity may still be resolving/importing it.";
            }

            if (m_unityAiAssistantInstallRequest != null && !m_unityAiAssistantInstallRequest.IsCompleted)
            {
                return "Install requested through Package Manager; waiting for completion.";
            }

            return "Not installed. Not required for local SGGMcp tool/skill compatibility; install only for optional Unity Assistant workflows.";
        }

        void BuildUnityAiAssistantPanel()
        {
            HelpRow(m_unityAiAssistantPanel, "Unity AI Assistant is optional compatibility guidance. Refresh and copy actions are safe; Install Optional Package mutates this Unity project's package manifest through Package Manager.");

            var packageSection = AddSection(m_unityAiAssistantPanel, "Unity AI Assistant MCP 2.8");
            m_unityAiAssistantBaseline = AddRow(packageSection, "Baseline");
            m_unityAiAssistantPackageStatus = AddRow(packageSection, "Official package");
            m_unityAiAssistantRelayStatus = AddRow(packageSection, "Official relay");
            var packageActions = AddActions(packageSection);
            AddButton(packageActions, "Install Optional Package", InstallUnityAiAssistantPackage);
            AddButton(packageActions, "Open Package Manager", OpenPackageManager);
            AddButton(packageActions, "Copy Package Id", () => CopyText("Unity AI Assistant package id", UnityAiAssistantPackageId));
            AddButton(packageActions, "Refresh", RefreshUnityAiAssistantAndLocalize);

            var noPackageSection = AddSection(m_unityAiAssistantPanel, "Works Without Official Package");
            AddRow(noPackageSection, "Statement", "SGGMcp works without com.unity.ai.assistant, Unity Assistant chat, Unity Cloud, credits, subscription, or official relay.");
            m_unityAiAssistantWorksWithoutOfficialPackage = AddRow(noPackageSection, "Local baseline");

            var packageAddsSection = AddSection(m_unityAiAssistantPanel, "Adds When Official Package Is Installed");
            m_unityAiAssistantAddsWhenOfficialPackageInstalled = AddRow(packageAddsSection, "Optional additions");

            var skillSourcesSection = AddSection(m_unityAiAssistantPanel, "Skill Sources And Import Flow");
            m_unityAiAssistantSkillsStatus = AddRow(skillSourcesSection, "Skills sources");
            m_unityAiAssistantDiscoverySources = AddRow(skillSourcesSection, "Discovery sources");
            m_unityAiAssistantImportSemantics = AddRow(skillSourcesSection, "Import semantics");
            var skillSourceActions = AddActions(skillSourcesSection);
            AddButton(skillSourceActions, "Copy Skill Sources Summary", CopyUnityAiAssistantSkillSourcesSummary);

            var toolsSection = AddSection(m_unityAiAssistantPanel, "Tool Compatibility");
            AddRow(toolsSection, "Rule", "Unity AI Assistant concepts map to command catalog entries invoked through sgg_unity_call; no new top-level MCP tools are added.");
            m_unityAiAssistantSupportedTools = AddRow(toolsSection, "Supported local parity");
            m_unityAiAssistantPartialTools = AddRow(toolsSection, "Partial local parity");
            m_unityAiAssistantDeferredTools = AddRow(toolsSection, "Deferred / diagnostic-only");
            var toolActions = AddActions(toolsSection);
            AddButton(toolActions, "Copy Tool Map", CopyUnityAiAssistantToolMap);
        }

        void BuildDiagnosticsPanel()
        {
            HelpRow(m_diagnosticsPanel, "Diagnostics is read-only troubleshooting output with secret redaction. Refresh updates displayed values and Copy Diagnostics copies redacted text only; Bridge token displays Present/Missing; MCP token displays Present/Not configured.");

            var pathsSection = AddSection(m_diagnosticsPanel, "Diagnostics Paths");
            m_diagnosticsRuntimePath = AddRow(pathsSection, "Runtime");
            m_diagnosticsConfigPath = AddRow(pathsSection, "Gateway config");
            m_diagnosticsPolicyPath = AddRow(pathsSection, "Policy file");
            m_diagnosticsLogPath = AddRow(pathsSection, "Gateway log");
            m_diagnosticsHeartbeatPath = AddRow(pathsSection, "Heartbeat");
            m_diagnosticsProblemReportsPath = AddRow(pathsSection, "Problem reports folder");

            var statusSection = AddSection(m_diagnosticsPanel, "Diagnostics Status");
            m_diagnosticsBridgeToken = AddRow(statusSection, "Bridge token");
            m_diagnosticsMcpToken = AddRow(statusSection, "MCP token");
            m_diagnosticsCanonicalPath = AddRow(statusSection, "CanonicalPath dependency");
            m_diagnosticsPathGuard = AddRow(statusSection, "PathGuard");
            m_diagnosticsFeatureFlags = AddRow(statusSection, "Feature flags");
            m_diagnosticsAuditStatus = AddRow(statusSection, "Audit log");
            m_diagnosticsAvailability = AddRow(statusSection, "Availability badges");
            m_diagnosticsWindowTiming = AddRow(statusSection, "MCP Window timing");
            m_diagnosticsRedaction = AddRow(statusSection, "Redaction", "Copy Diagnostics redacts authorization, token, API key, password and cookie-like values; token values are never included.");
            m_diagnosticsLastErrors = AddRow(statusSection, "Last errors");
            AddRow(statusSection, "MCP Server", "Gateway remains the only MCP server; this UI does not start any external MCP server.");
            var actions = AddActions(statusSection);
            AddButton(actions, "Refresh Diagnostics", RefreshDiagnosticsAndLocalize);
            AddButton(actions, "Copy Diagnostics", CopyDiagnostics);
            AddButton(actions, "Open Problem Reports Folder", OpenProblemReportsFolder);
        }

        void InstallUnityAiAssistantPackage()
        {
            var message = "Install optional Unity package '" + UnityAiAssistantPackageId + "'?\n\n"
                + "This package is not required for local SGGMcp compatibility. Install it only if you want Unity Assistant or official Unity package workflows in this project.";
            if (!EditorUtility.DisplayDialog("Install Unity AI Assistant", message, "Install", "Cancel"))
            {
                m_lastActionLabel.text = "Install Unity AI Assistant: cancelled.";
                return;
            }

            try
            {
                if (m_unityAiAssistantInstallRequest != null && !m_unityAiAssistantInstallRequest.IsCompleted)
                {
                    m_lastActionLabel.text = "Unity AI Assistant install is already running.";
                    return;
                }

                m_unityAiAssistantInstallRequest = UnityEditor.PackageManager.Client.Add(UnityAiAssistantPackageId);
                EditorApplication.update -= PollUnityAiAssistantInstall;
                EditorApplication.update += PollUnityAiAssistantInstall;
                m_lastActionLabel.text = "Unity AI Assistant package install requested: " + UnityAiAssistantPackageId;
                RefreshUnityAiAssistant();
            }
            catch (Exception exception)
            {
                m_lastActionLabel.text = "Install Unity AI Assistant: " + exception.Message;
                EditorUtility.DisplayDialog("Install Unity AI Assistant Failed", exception.Message, "OK");
            }
        }

        void PollUnityAiAssistantInstall()
        {
            if (m_unityAiAssistantInstallRequest == null || !m_unityAiAssistantInstallRequest.IsCompleted)
            {
                return;
            }

            EditorApplication.update -= PollUnityAiAssistantInstall;
            if (m_unityAiAssistantInstallRequest.Status == UnityEditor.PackageManager.StatusCode.Success)
            {
                m_lastActionLabel.text = "Unity AI Assistant package installed: " + m_unityAiAssistantInstallRequest.Result.version;
            }
            else
            {
                var errorMessage = m_unityAiAssistantInstallRequest.Error == null ? "unknown Package Manager error" : m_unityAiAssistantInstallRequest.Error.message;
                m_lastActionLabel.text = "Unity AI Assistant package install failed: " + errorMessage;
            }

            m_unityAiAssistantInstallRequest = null;
            RefreshUnityAiAssistant();
        }

        void OpenPackageManager()
        {
            if (!EditorApplication.ExecuteMenuItem("Window/Package Manager"))
            {
                EditorUtility.DisplayDialog("Open Package Manager", "Unity did not open the Package Manager menu item.", "OK");
            }

            m_lastActionLabel.text = "Package Manager opened. Optional package id: " + UnityAiAssistantPackageId;
        }

        void CopyUnityAiAssistantToolMap()
        {
            GUIUtility.systemCopyBuffer = "Unity AI Assistant MCP 2.8 compatibility\n\n"
                + "Supported local parity:\n" + UnityAiAssistantSupportedToolMap() + "\n\n"
                + "Partial local parity:\n" + UnityAiAssistantPartialToolMap() + "\n\n"
                + "Deferred / diagnostic-only:\n" + UnityAiAssistantDeferredToolMap();
            m_lastActionLabel.text = "Unity AI Assistant tool map copied to clipboard.";
        }

        void CopyUnityAiAssistantSkillSourcesSummary()
        {
            GUIUtility.systemCopyBuffer = "Unity AI Assistant compatible skill sources\n\n"
                + "Discovery sources:\n" + UnityAiAssistantSkillDiscoverySources() + "\n\n"
                + "Import semantics:\n" + UnityAiAssistantSkillImportSemantics() + "\n\n"
                + "Optional package additions:\n" + UnityAiAssistantPackageAddsSummary();
            m_lastActionLabel.text = "Unity AI Assistant skill sources summary copied to clipboard.";
        }

        void CopyDiagnostics()
        {
            try
            {
                RefreshDiagnostics();
                var diagnostics = new McpDiagnosticsCopyViewModel(
                    m_diagnosticsRuntimePath.text,
                    m_diagnosticsConfigPath.text,
                    m_diagnosticsPolicyPath.text,
                    m_diagnosticsLogPath.text,
                    m_diagnosticsHeartbeatPath.text,
                    m_diagnosticsProblemReportsPath.text,
                    m_diagnosticsBridgeToken.text,
                    m_diagnosticsMcpToken.text,
                    m_diagnosticsCanonicalPath.text,
                    m_diagnosticsPathGuard.text,
                    m_diagnosticsFeatureFlags.text,
                    m_diagnosticsAuditStatus.text,
                    m_diagnosticsWindowTiming.text,
                    m_diagnosticsLastErrors.text);
                GUIUtility.systemCopyBuffer = McpDiagnosticsCopyViewModel.Build(diagnostics);
                m_lastActionLabel.text = "Diagnostics copied to clipboard with secret redaction.";
            }
            catch (Exception exception)
            {
                m_lastActionLabel.text = "Copy Diagnostics: " + exception.Message;
                EditorUtility.DisplayDialog("Copy Diagnostics Failed", exception.Message, "OK");
            }
        }

        void OpenProblemReportsFolder()
        {
            try
            {
                var root = ProblemReportsRoot();
                if (!Directory.Exists(root))
                {
                    m_lastActionLabel.text = "No problem reports yet: " + root;
                    return;
                }

                EditorUtility.RevealInFinder(root);
                m_lastActionLabel.text = "Problem reports folder opened for manual attachment only: " + root;
                RefreshDiagnostics();
            }
            catch (Exception exception)
            {
                m_lastActionLabel.text = "Open Problem Reports Folder: " + exception.Message;
                EditorUtility.DisplayDialog("Open Problem Reports Folder Failed", exception.Message, "OK");
            }
        }

        void RefreshUnityAiAssistant()
        {
            if (m_unityAiAssistantBaseline == null)
            {
                return;
            }

            m_unityAiAssistantBaseline.text = "Cloudless SGGMcp baseline does not require Unity Assistant chat, Unity Cloud, credits, subscription or official relay.";
            m_unityAiAssistantWorksWithoutOfficialPackage.text = UnityAiAssistantWorksWithoutOfficialPackageSummary();
            m_unityAiAssistantAddsWhenOfficialPackageInstalled.text = UnityAiAssistantPackageAddsSummary();
            m_unityAiAssistantPackageStatus.text = UnityAiAssistantPackageStatus();
            m_unityAiAssistantRelayStatus.text = UnityAiAssistantRelayStatus();
            m_unityAiAssistantSkillsStatus.text = "Supported when enabled and reviewed; missing source folders are optional, not errors.";
            m_unityAiAssistantDiscoverySources.text = UnityAiAssistantSkillDiscoverySources();
            m_unityAiAssistantImportSemantics.text = UnityAiAssistantSkillImportSemantics();
            m_unityAiAssistantSupportedTools.text = UnityAiAssistantSupportedToolMap();
            m_unityAiAssistantPartialTools.text = UnityAiAssistantPartialToolMap();
            m_unityAiAssistantDeferredTools.text = UnityAiAssistantDeferredToolMap();
        }

        void RefreshUnityAiAssistantAndLocalize()
        {
            RefreshUnityAiAssistant();
            ApplyLocalization();
        }

        void RefreshDiagnosticsAndLocalize()
        {
            RefreshDiagnostics();
            ApplyLocalization();
        }

        void RefreshDiagnostics()
        {
            if (m_diagnosticsRuntimePath == null)
            {
                return;
            }

            var gatewayStatus = GatewayInstaller.GetStatus();
            var processStatus = GatewayProcessManager.GetStatus(false);
            var policyStatus = GatewayPolicyManager.GetStatus();
            var heartbeatPath = UnityBridgeRuntime.HeartbeatFilePath;
            var auditPath = Path.Combine(UnityMcpPackagePaths.LocalAppDataRoot, "audit", "audit.ndjson");
            var problemReportsPath = ProblemReportsRoot();
            m_diagnosticsRuntimePath.text = string.IsNullOrEmpty(gatewayStatus.RuntimeRoot) ? "Not installed" : gatewayStatus.RuntimeRoot;
            m_diagnosticsConfigPath.text = policyStatus.ConfigPath;
            m_diagnosticsPolicyPath.text = policyStatus.PolicyPath;
            m_diagnosticsLogPath.text = string.IsNullOrEmpty(processStatus.LogPath) ? processStatus.LogDirectory : processStatus.LogPath;
            m_diagnosticsHeartbeatPath.text = heartbeatPath;
            m_diagnosticsProblemReportsPath.text = Directory.Exists(problemReportsPath) ? problemReportsPath : "No problem reports yet: " + problemReportsPath;
            m_diagnosticsBridgeToken.text = UnityBridgeRuntime.HasBridgeToken ? "Present" : "Missing";
            m_diagnosticsMcpToken.text = McpTokenStatus();
            m_diagnosticsCanonicalPath.text = CanonicalPathDependencyStatusText() + "; " + CanonicalPathRegistryStatusText();
            m_diagnosticsPathGuard.text = "Available in Unity bridge path commands";
            m_diagnosticsFeatureFlags.text = ReadFeatureFlagsSummary(policyStatus.ConfigPath);
            m_diagnosticsAuditStatus.text = File.Exists(auditPath) ? "Present, " + new FileInfo(auditPath).Length + " bytes at " + auditPath : "Missing at " + auditPath;
            m_diagnosticsAvailability.text = "config=" + PresentMissing(File.Exists(policyStatus.ConfigPath))
                + "; policy=" + PresentMissing(File.Exists(policyStatus.PolicyPath))
                + "; log=" + PresentMissing(!string.IsNullOrEmpty(processStatus.LogPath) && File.Exists(processStatus.LogPath))
                + "; audit=" + PresentMissing(File.Exists(auditPath))
                + "; problem_reports=" + PresentMissing(Directory.Exists(problemReportsPath))
                + "; pathguard=Present";
            RefreshDiagnosticsTiming();
            m_diagnosticsRedaction.text = "Copy Diagnostics redacts authorization, token, API key, password and cookie-like values; Bridge token displays Present/Missing; MCP token displays Present/Not configured.";
            m_diagnosticsLastErrors.text = LastErrorLines(processStatus.LogTail);
        }

        string PresentMissing(bool present)
        {
            return present ? "Present" : "Missing";
        }

        internal static string LastErrorLines(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "No recent gateway log output";
            }

            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var result = string.Empty;
            var count = 0;
            for (var index = lines.Length - 1; index >= 0 && count < 3; index--)
            {
                if (lines[index].IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0
                    || lines[index].IndexOf("exception", StringComparison.OrdinalIgnoreCase) >= 0
                    || lines[index].IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result = lines[index].Trim() + (result.Length == 0 ? string.Empty : "\n" + result);
                    count++;
                }
            }

            return string.IsNullOrEmpty(result) ? "No recent errors in gateway log tail" : McpDiagnosticsCopyViewModel.Redact(result);
        }

        string ProblemReportsRoot()
        {
            return Path.Combine(UnityMcpPackagePaths.LocalAppDataRoot, "audit", "problem-reports");
        }

        string UnityAiAssistantWorksWithoutOfficialPackageSummary()
        {
            return "Available without official package: MCP connection, project discovery, command discovery, console/status, assets/scripts, project settings, scenes/editor metadata, package metadata, Unity AI tool parity via sgg_unity_call, and skills when enabled.";
        }

        string UnityAiAssistantPackageAddsSummary()
        {
            return "Adds optional official Unity Assistant UI, package resources/assets, package skill sources under Packages/**/AIAssistantSkills/**/SKILL.md, compatibility validation, and official relay status diagnostic-only visibility.";
        }

        string UnityAiAssistantSkillDiscoverySources()
        {
            return ".mcp/skills project-local reviewed skills; Assets/**/SKILL.md project assets; Packages/**/AIAssistantSkills/**/SKILL.md Unity AI Assistant compatible package skills; %APPDATA%/Unity/AIAssistantSkills user-level candidates; built-in SGGMcp examples. Missing source folders are optional, not errors.";
        }

        string UnityAiAssistantSkillImportSemantics()
        {
            return "Use skill.import dry_run to preview a reviewed copy into .mcp/skills/<target_name>, then repeat with policy approval when intended. Import writes review_required metadata only; it does not auto-enable skills, mark them reviewed, change policy, or launch the official relay.";
        }

        string UnityAiAssistantRelayStatus()
        {
            var envPath = Environment.GetEnvironmentVariable("UNITY_MCP_RELAY_PATH");
            if (!string.IsNullOrEmpty(envPath))
            {
                return (File.Exists(envPath) || Directory.Exists(envPath) ? "Configured candidate found: " : "Configured candidate missing: ") + envPath + ". Diagnostic-only; use unity.relay.status from Gateway.";
            }

            var homeRelayRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".unity", "relay");
            return Directory.Exists(homeRelayRoot)
                ? "Candidate root found: " + homeRelayRoot + ". Diagnostic-only; local SGGMcp commands do not depend on it."
                : "No relay configured. This is OK for the cloudless SGGMcp baseline; unity.relay.status remains diagnostic-only.";
        }

        string UnityAiAssistantSupportedToolMap()
        {
            return "Unity_ListResources -> asset.list\n"
                + "Unity_ReadResource -> asset.text.read\n"
                + "Unity_FindInFile -> asset.text.find\n"
                + "Unity.FindFiles / Unity.Grep -> asset.search / asset.text.search\n"
                + "Unity_GetSha -> script.hash\n"
                + "Unity_ValidateScript -> script.validate\n"
                + "Unity_ManageScript_capabilities -> script.capabilities\n"
                + "Unity_ApplyTextEdits -> script.text_edits.apply\n"
                + "unity://spec/script-edits -> script.text_edits.spec\n"
                + "Unity.GetStaticProjectSettingsTool -> project.settings.summary\n"
                + "Unity.GetProjectSettings -> project.settings.inspect\n"
                + "Unity.GetProjectData -> project.assets.taxonomy\n"
                + "Unity.PackageManager.GetData -> package.info / package.list\n"
                + "Unity.GetObjectData -> asset.serialized.inspect\n"
                + "Unity.GameObject.GetBuiltinAssets -> gameobject.builtin_assets\n"
                + "Unity.Camera.GetVisibleObjects -> scene.visible_objects\n"
                + "Unity.GameObject.GetGameObjectBounds -> gameobject.bounds\n"
                + "Unity_ReadConsole -> logs.console.read / logs.console.tail / sgg_unity_logs\n"
                + "Unity skills -> skill.list / skill.read / skill.validate / skill.import";
        }

        string UnityAiAssistantPartialToolMap()
        {
            return "Unity_ManageScene -> scene.active/build_settings/loaded/hierarchy.active/open/save/create/hierarchy/search/inspect\n"
                + "Unity_ManageGameObject -> gameobject.*, component.*, material.* with feature flags and policy\n"
                + "Unity_ManageAsset -> asset.info/components.list/serialized.inspect/import/list/search/text.search/references/dependencies/guid/risk_report/move/delete\n"
                + "Unity_ManageEditor / Unity_ManageMenuItem -> editor state/selection/prefab-stage/active-tool/windows/menu/tags/layers read-only metadata plus policy-gated menu execute\n"
                + "Unity_ManageScript -> script.read/hash/validate/text_edits.apply/write";
        }

        string UnityAiAssistantDeferredToolMap()
        {
            return "Unity_ImportExternalModel -> deferred/deny unless explicit network/import policy is designed\n"
                + "Unity_ScriptApplyEdits structured edits -> deferred beyond bounded text edits\n"
                + "Unity_ManageShader write flows -> deferred; text read/search already covered by asset.text.*\n"
                + "Unity_RunCommand / dynamic execution -> prefer specific SGGMcp commands; execute-risk only behind policy\n"
                + "Unity.AssetGeneration.* / WebFetch / cloud features -> not baseline\n"
                + "Official Unity relay -> unity.relay.status diagnostic-only; no launch from this tab";
        }
    }
}
