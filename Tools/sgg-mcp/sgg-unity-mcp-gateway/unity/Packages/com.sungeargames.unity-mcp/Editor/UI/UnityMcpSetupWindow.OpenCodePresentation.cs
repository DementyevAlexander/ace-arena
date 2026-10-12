using System;
using System.IO;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Paths;
using SGG.UnityMCP.Editor.Setup;
using UnityEditor;
using UnityEngine.UIElements;

namespace SGG.UnityMCP.Editor.UI
{
    public sealed partial class UnityMcpSetupWindow
    {
        const string PresentationRestartSessionPrefix = "SGG.UnityMCP.OpenCodePresentation.Restart.";
        const string PresentationUncertainSessionPrefix = "SGG.UnityMCP.OpenCodePresentation.Uncertain.";

        Label m_openCodePresentationWorkspace;
        Label m_openCodePresentationArtifact;
        Label m_openCodePresentationConfig;
        Label m_openCodePresentationOpenCode;
        Label m_openCodePresentationOpenChamber;
        Label m_openCodePresentationRenderer;
        Label m_openCodePresentationRestart;
        Button m_openCodePresentationPreviewButton;
        Button m_openCodePresentationInstallButton;
        Button m_openCodePresentationRemoveButton;
        Button m_openCodePresentationOpenConfigButton;
        OpenCodePresentationSetupManager.PresentationStatus m_openCodePresentationStatus;
        OpenCodePresentationSetupManager.ClientProbeStatus m_openCodePresentationClientStatus;
        string m_openCodePresentationRequestedWorkspace = string.Empty;

        void BuildOpenCodePresentationSetup(VisualElement parent)
        {
            HelpRow(parent, "OpenCode v2/OpenChamber v2 Presentation is optional and Web/PWA-first. It adds bounded completed summaries without changing canonical MCP args or results. Rendering and live labels depend on the installed client; an upstream-only capability is not shipped support.");
            m_openCodePresentationWorkspace = OptionalToolRow(parent, "Presentation workspace", "Not checked.");
            m_openCodePresentationWorkspace.name = "setup-opencode-presentation-workspace";
            m_openCodePresentationArtifact = OptionalToolRow(parent, "Presentation artifact", "Not checked. Click Refresh Presentation Status.");
            m_openCodePresentationArtifact.name = "setup-opencode-presentation-artifact";
            m_openCodePresentationConfig = OptionalToolRow(parent, "Presentation config", "Not checked.");
            m_openCodePresentationConfig.name = "setup-opencode-presentation-config";
            m_openCodePresentationOpenCode = OptionalToolRow(parent, "OpenCode version", "Not checked. OpenCode v2 plugin API required.");
            m_openCodePresentationOpenCode.name = "setup-opencode-presentation-opencode-version";
            m_openCodePresentationOpenChamber = OptionalToolRow(parent, "OpenChamber version", "Not checked. OpenChamber v2 renderer support must be verified.");
            m_openCodePresentationOpenChamber.name = "setup-opencode-presentation-openchamber-version";
            m_openCodePresentationRenderer = OptionalToolRow(parent, "Web renderer support", "OpenCode v2/OpenChamber v2: verify completed summaries and live labels in the installed renderer. Native human approval support is separate and unchanged.");
            m_openCodePresentationRenderer.name = "setup-opencode-presentation-renderer";
            m_openCodePresentationRestart = OptionalToolRow(parent, "Presentation restart", "No workspace change applied in this Editor session.");
            m_openCodePresentationRestart.name = "setup-opencode-presentation-restart";

            var actions = AddActions(parent);
            var refreshButton = AddButton(actions, "Refresh Presentation Status", () => RunAction("Refresh Presentation Status", RefreshOpenCodePresentationStatus));
            refreshButton.name = "setup-opencode-presentation-refresh";
            m_openCodePresentationPreviewButton = AddButton(actions, "Preview Presentation Install/Update", () => RunAction("Preview Presentation Install/Update", PreviewOpenCodePresentationInstall));
            m_openCodePresentationPreviewButton.name = "setup-opencode-presentation-preview";
            m_openCodePresentationInstallButton = AddButton(actions, "Install/Update Presentation", () => RunAction("Install/Update Presentation", InstallOpenCodePresentation));
            m_openCodePresentationInstallButton.name = "setup-opencode-presentation-install";
            m_openCodePresentationRemoveButton = AddButton(actions, "Remove Presentation", RemoveOpenCodePresentationWithConfirmation);
            m_openCodePresentationRemoveButton.name = "setup-opencode-presentation-remove";
            m_openCodePresentationOpenConfigButton = AddButton(actions, "Open Presentation Config", () => RunAction("Open Presentation Config", OpenOpenCodePresentationConfig));
            m_openCodePresentationOpenConfigButton.name = "setup-opencode-presentation-open-config";
            var copyButton = AddButton(actions, "Copy Redacted Presentation Diagnostics", CopyOpenCodePresentationDiagnostics);
            copyButton.name = "setup-opencode-presentation-copy-diagnostics";
        }

        void RefreshOpenCodePresentationSetup()
        {
            if (m_openCodePresentationWorkspace == null)
            {
                return;
            }

            var workspace = CurrentOpenCodePresentationWorkspace();
            var mutationOutcomeUncertain = PresentationMutationOutcomeUncertainForWorkspace(workspace);
            var restartPending = PresentationRestartPendingForWorkspace(workspace);
            m_openCodePresentationWorkspace.text = string.IsNullOrEmpty(workspace)
                ? "Unavailable - select a valid OpenCode workspace in MCP Connection."
                : workspace + " (root OpenCode config, project-scoped .opencode artifact)" + (PresentationWorkspaceRequiresConfirmation() ? " Warning: confirm this automatic fallback before mutation." : string.Empty);
            var status = PresentationStatusForWorkspace(workspace);
            if (status == null)
            {
                m_openCodePresentationArtifact.text = "Not checked for the selected workspace. Click Refresh Presentation Status.";
                m_openCodePresentationConfig.text = "Not checked. The lifecycle preserves existing root MCP settings and manages only its plugin entry.";
                m_openCodePresentationRestart.text = mutationOutcomeUncertain
                    ? "Unknown outcome - refresh status before retrying; restart the client if the change was applied."
                    : restartPending
                        ? "Required - restart OpenCode/OpenChamber to load the applied presentation change. Read-only refresh does not clear this reminder."
                        : "No workspace change applied in this Editor session.";
            }
            else
            {
                var installed = string.IsNullOrEmpty(status.InstalledVersion) ? "none" : status.InstalledVersion;
                m_openCodePresentationArtifact.text = "state=" + status.ArtifactState + ", bundled=" + status.BundledVersion + ", installed=" + installed + ", " + status.BundledHash;
                m_openCodePresentationConfig.text = "state=" + status.ConfigState + ", file=" + status.ConfigFile + ", owned entries=" + status.OwnedEntries;
                m_openCodePresentationRestart.text = mutationOutcomeUncertain
                    ? "Unknown outcome - refresh status before retrying; restart the client if the change was applied."
                    : restartPending
                        ? "Required - restart OpenCode/OpenChamber to load the applied presentation change. Read-only refresh does not clear this reminder."
                        : "Not required by an applied presentation change in this Editor session.";
            }

            m_openCodePresentationOpenCode.text = m_openCodePresentationClientStatus?.OpenCode ?? "Not checked. OpenCode v2 plugin API required.";
            m_openCodePresentationOpenChamber.text = m_openCodePresentationClientStatus?.OpenChamber ?? "Not checked. OpenChamber v2 renderer support must be verified.";
            var workspaceReady = !string.IsNullOrEmpty(workspace);
            var mutationAllowed = PresentationMutationAllowed(workspaceReady, mutationOutcomeUncertain);
            m_openCodePresentationPreviewButton?.SetEnabled(mutationAllowed);
            m_openCodePresentationInstallButton?.SetEnabled(mutationAllowed);
            m_openCodePresentationRemoveButton?.SetEnabled(mutationAllowed);
            m_openCodePresentationOpenConfigButton?.SetEnabled(workspaceReady && status != null && status.ConfigState != "missing");
        }

        NodeRuntimeInstaller.InstallResult RefreshOpenCodePresentationStatus()
        {
            if (!TryResolvePresentationActionWorkspace(out var workspace, out var failure)) return failure;
            var refreshed = OpenCodePresentationSetupManager.Run(workspace, OpenCodePresentationSetupManager.SetupAction.Status, CurrentPresentationWorkspaceHash());
            if (!refreshed.Parsed && PresentationMutationOutcomeUncertainForWorkspace(workspace)) return NodeRuntimeInstaller.InstallResult.Fail(refreshed.Error);
            CacheOpenCodePresentationStatus(refreshed, workspace, true);
            m_openCodePresentationClientStatus = OpenCodePresentationSetupManager.ProbeClients(workspace);
            return m_openCodePresentationStatus.Parsed
                ? NodeRuntimeInstaller.InstallResult.Ok("Presentation status refreshed. " + m_openCodePresentationStatus.Summary())
                : NodeRuntimeInstaller.InstallResult.Fail(m_openCodePresentationStatus.Error);
        }

        NodeRuntimeInstaller.InstallResult PreviewOpenCodePresentationInstall()
        {
            if (!TryResolvePresentationActionWorkspace(out var workspace, out var failure)) return failure;
            if (PresentationMutationOutcomeUncertainForWorkspace(workspace)) return NodeRuntimeInstaller.InstallResult.Fail("Refresh Presentation Status before previewing or retrying an uncertain mutation.");
            CacheOpenCodePresentationStatus(OpenCodePresentationSetupManager.Run(workspace, OpenCodePresentationSetupManager.SetupAction.DryRun, CurrentPresentationWorkspaceHash()), workspace);
            if (!m_openCodePresentationStatus.Parsed)
            {
                return NodeRuntimeInstaller.InstallResult.Fail(m_openCodePresentationStatus.Error);
            }
            GUIUtilityBridge.CopyText(m_openCodePresentationStatus.RawJson);
            return m_openCodePresentationStatus.CommandSucceeded
                ? NodeRuntimeInstaller.InstallResult.Ok("Copied bounded presentation dry-run JSON. " + m_openCodePresentationStatus.Summary())
                : NodeRuntimeInstaller.InstallResult.Fail(m_openCodePresentationStatus.Summary());
        }

        NodeRuntimeInstaller.InstallResult InstallOpenCodePresentation()
        {
            if (!TryResolvePresentationActionWorkspace(out var workspace, out var failure, true, "Install/Update")) return failure;
            if (PresentationMutationOutcomeUncertainForWorkspace(workspace)) return NodeRuntimeInstaller.InstallResult.Fail("Refresh Presentation Status before retrying an uncertain mutation.");
            CacheOpenCodePresentationStatus(OpenCodePresentationSetupManager.Run(workspace, OpenCodePresentationSetupManager.SetupAction.Write, CurrentPresentationWorkspaceHash()), workspace);
            return m_openCodePresentationStatus.CommandSucceeded
                ? NodeRuntimeInstaller.InstallResult.Ok("Presentation lifecycle completed. " + m_openCodePresentationStatus.Summary())
                : NodeRuntimeInstaller.InstallResult.Fail(m_openCodePresentationStatus.Error);
        }

        void RemoveOpenCodePresentationWithConfirmation()
        {
            if (!TryResolvePresentationActionWorkspace(out var workspace, out var failure))
            {
                RunAction("Remove Presentation", () => failure);
                return;
            }
            if (PresentationMutationOutcomeUncertainForWorkspace(workspace))
            {
                RunAction("Remove Presentation", () => NodeRuntimeInstaller.InstallResult.Fail("Refresh Presentation Status before retrying an uncertain mutation."));
                return;
            }
            var fallbackWarning = PresentationWorkspaceRequiresConfirmation()
                ? "\n\nThis is an automatic fallback because the saved/explicit workspace was unavailable. Confirm this exact target before removal."
                : string.Empty;
            if (!EditorUtility.DisplayDialog(
                    "Remove OpenCode/OpenChamber Presentation",
                    "Remove only the SGGMcp-owned presentation artifact and exact config entry from this workspace?\n\n" + workspace + fallbackWarning,
                    "Remove",
                    "Cancel"))
            {
                return;
            }
            RunAction("Remove Presentation", () => RemoveOpenCodePresentation(workspace));
        }

        NodeRuntimeInstaller.InstallResult RemoveOpenCodePresentation(string workspace)
        {
            CacheOpenCodePresentationStatus(OpenCodePresentationSetupManager.Run(workspace, OpenCodePresentationSetupManager.SetupAction.Remove, CurrentPresentationWorkspaceHash()), workspace);
            return m_openCodePresentationStatus.CommandSucceeded
                ? NodeRuntimeInstaller.InstallResult.Ok("Presentation removal completed. " + m_openCodePresentationStatus.Summary())
                : NodeRuntimeInstaller.InstallResult.Fail(m_openCodePresentationStatus.Error);
        }

        NodeRuntimeInstaller.InstallResult OpenOpenCodePresentationConfig()
        {
            if (!TryResolvePresentationActionWorkspace(out var workspace, out var failure)) return failure;
            return OpenCodePresentationSetupManager.OpenConfig(workspace, PresentationStatusForWorkspace(workspace));
        }

        void CopyOpenCodePresentationDiagnostics()
        {
            var status = PresentationStatusForWorkspace(CurrentOpenCodePresentationWorkspace());
            CopyText("Redacted presentation diagnostics", status == null
                ? "{\n  \"schema_version\": 1,\n  \"setup\": \"opencode-presentation\",\n  \"status\": \"not_checked\"\n}"
                : status.RedactedDiagnostics());
        }

        string CurrentOpenCodePresentationWorkspace()
        {
            var status = m_openCodePresentationNetworkStatus;
            return status != null && status.OpenCodeTargetAvailable ? status.OpenCodeWorkspaceRoot ?? string.Empty : string.Empty;
        }

        OpenCodePresentationSetupManager.PresentationStatus PresentationStatusForWorkspace(string workspace)
        {
            if (m_openCodePresentationStatus == null || string.IsNullOrEmpty(workspace) || string.IsNullOrEmpty(m_openCodePresentationRequestedWorkspace))
            {
                return null;
            }
            try
            {
                var comparison = Environment.OSVersion.Platform == PlatformID.Win32NT ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                return string.Equals(Path.GetFullPath(workspace), Path.GetFullPath(m_openCodePresentationRequestedWorkspace), comparison)
                    ? m_openCodePresentationStatus
                    : null;
            }
            catch
            {
                return null;
            }
        }

        bool TryResolvePresentationActionWorkspace(
            out string workspace,
            out NodeRuntimeInstaller.InstallResult failure,
            bool confirmFallback = false,
            string action = "Change")
        {
            workspace = string.Empty;
            failure = null;
            GatewayNetworkModeManager.NetworkStatus status;
            try
            {
                status = GatewayNetworkModeManager.GetStatus(CurrentConnectionMode());
                m_openCodePresentationNetworkStatus = status;
            }
            catch (Exception exception)
            {
                failure = NodeRuntimeInstaller.InstallResult.Fail("Could not resolve the OpenCode workspace: " + exception.Message);
                return false;
            }
            if (!status.OpenCodeTargetAvailable || string.IsNullOrEmpty(status.OpenCodeWorkspaceRoot))
            {
                failure = NodeRuntimeInstaller.InstallResult.Fail("Select a valid OpenCode workspace first.");
                return false;
            }
            workspace = status.OpenCodeWorkspaceRoot;
            if (confirmFallback && (status.OpenCodeRequiresConfirmation || status.OpenCodeSavedWorkspaceRejected))
            {
                var confirmed = EditorUtility.DisplayDialog(
                    "Confirm Presentation Workspace",
                    action + " the optional presentation integration only in this exact automatic fallback workspace?\n\n" + workspace + "\n\nThe saved/explicit workspace was unavailable or no containing Git worktree was confirmed.",
                    action,
                    "Cancel");
                if (!confirmed)
                {
                    failure = NodeRuntimeInstaller.InstallResult.Ok("Presentation workspace change cancelled; no files changed.");
                    return false;
                }
            }
            return true;
        }

        bool PresentationWorkspaceRequiresConfirmation()
        {
            return m_openCodePresentationNetworkStatus != null
                && (m_openCodePresentationNetworkStatus.OpenCodeRequiresConfirmation || m_openCodePresentationNetworkStatus.OpenCodeSavedWorkspaceRejected);
        }

        void CacheOpenCodePresentationStatus(OpenCodePresentationSetupManager.PresentationStatus status, string requestedWorkspace, bool authoritativeStatus = false)
        {
            var normalized = NormalizePresentationWorkspace(requestedWorkspace);
            m_openCodePresentationStatus = status;
            m_openCodePresentationRequestedWorkspace = normalized;
            if (status != null && (status.RestartRequired || status.OutcomeUncertain)) SetPresentationSessionFlag(PresentationRestartSessionPrefix, normalized, true);
            if (status != null && status.OutcomeUncertain) SetPresentationSessionFlag(PresentationUncertainSessionPrefix, normalized, true);
            else if (authoritativeStatus && status != null && status.Parsed) SetPresentationSessionFlag(PresentationUncertainSessionPrefix, normalized, false);
        }

        internal static bool PreservePresentationRestartPending(bool current, OpenCodePresentationSetupManager.PresentationStatus status)
        {
            return current || (status != null && (status.RestartRequired || status.OutcomeUncertain));
        }

        internal static bool PresentationMutationAllowed(bool workspaceReady, bool outcomeUncertain)
        {
            return workspaceReady && !outcomeUncertain;
        }

        internal void CacheOpenCodePresentationStatusForCommand(OpenCodePresentationSetupManager.PresentationStatus status, string requestedWorkspace, bool authoritativeStatus = false)
        {
            CacheOpenCodePresentationStatus(status, requestedWorkspace, authoritativeStatus);
        }

        internal bool OpenCodePresentationRestartPendingForCommand(string workspace = null)
        {
            return PresentationRestartPendingForWorkspace(workspace ?? m_openCodePresentationRequestedWorkspace);
        }

        internal bool OpenCodePresentationOutcomeUncertainForCommand(string workspace)
        {
            return PresentationMutationOutcomeUncertainForWorkspace(workspace);
        }

        internal static void ClearOpenCodePresentationSessionStateForCommand(string workspace)
        {
            SetPresentationSessionFlag(PresentationRestartSessionPrefix, workspace, false);
            SetPresentationSessionFlag(PresentationUncertainSessionPrefix, workspace, false);
        }

        string CurrentPresentationWorkspaceHash()
        {
            return m_openCodePresentationNetworkStatus?.OpenCodeWorkspaceHash ?? string.Empty;
        }

        bool PresentationMutationOutcomeUncertainForWorkspace(string workspace)
        {
            return GetPresentationSessionFlag(PresentationUncertainSessionPrefix, workspace);
        }

        bool PresentationRestartPendingForWorkspace(string workspace)
        {
            return GetPresentationSessionFlag(PresentationRestartSessionPrefix, workspace);
        }

        static string NormalizePresentationWorkspace(string workspace)
        {
            try
            {
                return Path.GetFullPath(workspace ?? string.Empty);
            }
            catch
            {
                return workspace ?? string.Empty;
            }
        }

        static bool GetPresentationSessionFlag(string prefix, string workspace)
        {
            var key = PresentationWorkspaceSessionKey(prefix, workspace);
            return !string.IsNullOrEmpty(key) && SessionState.GetBool(key, false);
        }

        static void SetPresentationSessionFlag(string prefix, string workspace, bool value)
        {
            var key = PresentationWorkspaceSessionKey(prefix, workspace);
            if (string.IsNullOrEmpty(key)) return;
            if (value) SessionState.SetBool(key, true);
            else SessionState.EraseBool(key);
        }

        static string PresentationWorkspaceSessionKey(string prefix, string workspace)
        {
            try
            {
                var canonical = new CanonicalPathService().NormalizeProjectRoot(workspace).Value;
                return prefix + GatewayNetworkModeManager.OpenCodeWorkspaceIdentityHash(canonical);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
