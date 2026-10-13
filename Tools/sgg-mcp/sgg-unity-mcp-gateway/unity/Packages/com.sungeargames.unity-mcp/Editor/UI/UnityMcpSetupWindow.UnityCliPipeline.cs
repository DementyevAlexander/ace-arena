using System;
using System.IO;
using System.Threading.Tasks;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Setup;
using SGG.UnityMCP.Editor.UI.Localization;
using SGG.UnityMCP.Editor.UI.State;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace SGG.UnityMCP.Editor.UI
{
    public sealed partial class UnityMcpSetupWindow
    {
        Label m_windowsUnityCliStatus;
        Label m_unityCliNextAction;
        Label m_pipelineInstallState;
        Label m_windowsUnityCliPath;
        Label m_wslUnityCliStatus;
        Label m_wslUnityCliPath;
        Label m_unityCliEnvironmentMatch;
        Label m_pipelinePackageStatus;
        Label m_pipelineReadiness;
        Label m_pipelineConnectionStatus;
        Label m_setupUnityCliSkillState;
        Label m_updateCheckSummary;
        Label m_windowsUnityCliUpdateStatus;
        Label m_pipelineUpdateStatus;
        Label m_unityCliSkillUpdateStatus;
        Label m_pipelineAvailableVersion;
        Label m_unityCliSkillVersion;
        Label m_unityCliSkillAvailableVersion;
        Button m_installPipelineButton;
        Button m_probeUnityCliButton;
        Button m_installWindowsUnityCliButton;
        Button m_setupInstallUnityCliSkillButton;
        Button m_installWslWrapperButton;
        Button m_verifyPipelineButton;
        Button m_copyWslWrapperButton;
        Label m_projectUnityCliSkillStatus;
        Label m_projectUnityCliSkillPath;
        Label m_projectUnityCliSkillRuntime;
        Button m_installUnityCliSkillButton;
        Button m_updateUnityCliSkillButton;
        Button m_checkUnityToolUpdatesButton;
        Button m_updateWindowsUnityCliButton;
        Button m_updatePipelineButton;
        Button m_checkPipelineUpdatesButton;
        Button m_checkUnityCliSkillUpdatesButton;
        Button m_setupUpdateUnityCliSkillButton;
        const string UnityCliSkillUpdateDisabledReason = "Update disabled: check the official latest version first. Only an unmodified, source-verified older Unity CLI skill can be replaced.";
        static BoundedProcessOperation s_pipelineOperation;
        static BoundedProcessOperation s_windowsCliInstallOperation;
        static bool s_windowsCliProbeRunning;
        static BoundedProcessOperation s_pipelineVerificationOperation;
        static BoundedProcessOperation s_unityCliSkillOperation;
        static UnityCliProjectSkillInstaller.UnityCliSkillUpdateBinding s_unityCliSkillUpdateBinding;
        static BoundedProcessOperation s_wslWrapperOperation;
        static UnityCliUpdateCheckCoordinator s_unityToolUpdateCheck;
        static bool s_unityToolUpdateCheckAutomatic;
        static BoundedProcessOperation s_unityToolLocalRefreshOperation;
        static WindowsCliUpdateCoordinator s_windowsCliUpdateOperation;
        static UnityPipelineUpdateCoordinator s_pipelineUpdateOperation;
        bool m_unityToolFreshnessVisibilityHandled;

        void BuildUnityCliPipelineSection(VisualElement parent)
        {
            var section = AddSection(parent, "Unity CLI / Pipeline / Skill Updates");
            HelpRow(section, "These three tools are optional for SGGMcp. Blue: latest checked version. Red: a known update is available. Yellow diamond: not installed. Purple: unknown or informational. Install and update always require your confirmation; automatic checks skip missing components.");
            HelpRow(section, "Use each block's check button to show its available version. Pipeline and Skill can be checked before installation; this read-only check never installs anything. Skill versions are official Unity commits, not assumed CLI-version compatibility.");
            m_updateCheckSummary = AddRow(section, "Installed tool freshness");
            m_updateCheckSummary.name = "unity-tool-summary";

            var cli = AddSection(section, "CLI");
            cli.name = "unity-tool-cli";
            m_windowsUnityCliStatus = UnityToolStatusRow(cli, "Windows Unity CLI");
            m_windowsUnityCliPath = AddRow(cli, "Windows CLI executable");
            m_windowsUnityCliUpdateStatus = UnityToolStatusRow(cli, "Windows CLI update");
            m_windowsUnityCliUpdateStatus.name = "unity-tool-cli-update";
            var cliActions = AddActions(cli);
            m_installWindowsUnityCliButton = AddButton(cliActions, "Install Windows CLI", InstallWindowsUnityCli);
            m_checkUnityToolUpdatesButton = AddButton(cliActions, "Check CLI Updates", () => CheckUnityToolUpdates(UnityToolUpdateComponents.WindowsCli));
            m_updateWindowsUnityCliButton = AddButton(cliActions, "Update Windows CLI", UpdateWindowsUnityCli);
            AddButton(cliActions, "Refresh Local State", RefreshUnityToolLocalState);

            var pipeline = AddSection(section, "Pipeline");
            pipeline.name = "unity-tool-pipeline";
            m_pipelinePackageStatus = UnityToolStatusRow(pipeline, "Pipeline package");
            m_pipelineReadiness = UnityToolStatusRow(pipeline, "Pipeline readiness");
            m_pipelineConnectionStatus = UnityToolStatusRow(pipeline, "Official Pipeline connection", "Not verified. This is separate from SGGMcp bridge health.");
            m_pipelineUpdateStatus = UnityToolStatusRow(pipeline, "Pipeline update");
            m_pipelineUpdateStatus.name = "unity-tool-pipeline-update";
            m_pipelineAvailableVersion = UnityToolStatusRow(pipeline, "Available Pipeline version");
            var pipelineActions = AddActions(pipeline);
            m_installPipelineButton = AddButton(pipelineActions, "Install Pipeline", InstallUnityPipeline);
            m_checkPipelineUpdatesButton = AddButton(pipelineActions, "Check Pipeline Updates", () => CheckUnityToolUpdates(UnityToolUpdateComponents.Pipeline));
            m_updatePipelineButton = AddButton(pipelineActions, "Update Pipeline", UpdateUnityPipeline);
            m_verifyPipelineButton = AddButton(pipelineActions, "Verify Pipeline Connection", VerifyUnityPipeline);
            AddButton(pipelineActions, "Refresh Local State", RefreshUnityToolLocalState);

            var skill = AddSection(section, "Skill");
            skill.name = "unity-tool-skill";
            HelpRow(skill, "Project-scoped instructions for agents, not a CLI executable or Pipeline package. Use Unity's official latest skill version; immutable source and downloaded-content integrity are verified automatically before copying.");
            m_setupUnityCliSkillState = UnityToolStatusRow(skill, "Unity CLI agent skill");
            m_unityCliSkillVersion = UnityToolStatusRow(skill, "Installed skill version (commit)");
            m_unityCliSkillAvailableVersion = UnityToolStatusRow(skill, "Available official skill (commit)");
            m_unityCliSkillUpdateStatus = UnityToolStatusRow(skill, "Unity CLI skill update");
            var skillActions = AddActions(skill);
            m_setupInstallUnityCliSkillButton = AddButton(skillActions, "Install Unity CLI Skill", InstallUnityCliProjectSkill);
            m_checkUnityCliSkillUpdatesButton = AddButton(skillActions, "Check Skill Updates", () => CheckUnityToolUpdates(UnityToolUpdateComponents.Skill));
            m_setupUpdateUnityCliSkillButton = AddButton(skillActions, "Update Unity CLI Skill", UpdateUnityCliProjectSkill);
            AddButton(skillActions, "Refresh Unity CLI Skill", RefreshSkillsAndLocalize);
            AddButton(skillActions, "Open Skill Details", () => SelectTab("Skills"));

            var wsl = AddSection(section, "WSL (optional)");
            wsl.name = "unity-tool-wsl";
            HelpRow(wsl, "WSL is Windows Subsystem for Linux: a separate Linux environment used by some agents. It is not required by SGGMcp. A Linux Unity CLI is not the Windows CLI used for this Editor's Pipeline. A wrapper can forward to the exact Windows executable; detection never installs it or changes your shell.");
            m_wslUnityCliStatus = UnityToolStatusRow(wsl, "WSL unity resolution");
            m_wslUnityCliPath = AddRow(wsl, "WSL command path");
            m_unityCliEnvironmentMatch = UnityToolStatusRow(wsl, "CLI environment match");
            var wslActions = AddActions(wsl);
            m_probeUnityCliButton = AddButton(wslActions, "Refresh CLI / WSL Detection", ProbeUnityCliEnvironments);
            AddButton(AddActions(section), "Copy Update Diagnostics", CopyUnityToolUpdateDiagnostics);
        }

        Label UnityToolStatusRow(VisualElement parent, string key, string value = "Not checked.")
        {
            var label = AddChecklistRow(parent, key, "optional", value);
            label.parent.parent.AddToClassList("mcp-unity-tool-row");
            SetChecklistState(label, "info");
            return label;
        }

        internal static string UnityToolFreshnessState(bool knownAbsent, ComponentUpdateStatus update)
        {
            if (knownAbsent) return "optional";
            if (update == null || update.CheckState != UnityCliUpdateCheck.CheckStateChecked) return "info";
            if (update.UpdateState == UnityCliUpdateCheck.UpdateStateAvailable) return "error";
            return update.UpdateState == UnityCliUpdateCheck.UpdateStateCurrent ? "ok" : "info";
        }

        internal static string UnityCliSkillDisplayState(UnityCliProjectSkillInstaller.UnityCliSkillStatus skill, ComponentUpdateStatus update)
        {
            if (skill == null) return "info";
            if (!skill.Exists && skill.LocalState == UnityCliProjectSkillInstaller.LocalStateAbsent) return "optional";
            if (!skill.Valid) return "info";
            if (skill.LocalState == UnityCliProjectSkillInstaller.LocalStatePreviousReviewed) return "error";
            return UnityToolFreshnessState(false, update);
        }

        void BuildUnityCliSkillSection(VisualElement parent)
        {
            var section = AddSection(parent, "Official Unity CLI Agent Skill");
            HelpRow(section, "Install or update Unity's official latest project-scoped unity-cli skill after confirmation and automatic immutable-source/content verification. Existing unmanaged or modified targets are never overwritten.");
            m_projectUnityCliSkillStatus = UnityToolStatusRow(section, "Project unity-cli skill");
            m_projectUnityCliSkillPath = AddRow(section, "Project skill path");
            m_projectUnityCliSkillRuntime = AddRow(section, "Install runtime");
            AddRow(section, "Install source", UnityCliProjectSkillInstaller.ReviewedSource + "; exact official content hash verification, project copy, OpenCode + Codex.");
            var actions = AddActions(section);
            m_installUnityCliSkillButton = AddButton(actions, "Install Unity CLI Skill", InstallUnityCliProjectSkill);
            m_updateUnityCliSkillButton = AddButton(actions, "Update Unity CLI Skill", UpdateUnityCliProjectSkill);
            m_updateUnityCliSkillButton.SetEnabled(false);
            m_updateUnityCliSkillButton.tooltip = UnityCliSkillUpdateDisabledReason;
            AddButton(actions, "Refresh Unity CLI Skill", RefreshSkillsAndLocalize);
            AddButton(actions, "Open Project Skill Folder", OpenUnityCliProjectSkillFolder);
        }

        void RefreshUnityCliPipelineSetup()
        {
            if (m_windowsUnityCliStatus == null)
            {
                return;
            }

            var windows = UnityCliPipelineSetup.WindowsStatus;
            var wsl = UnityCliPipelineSetup.WslStatus;
            var pipeline = UnityCliPipelineSetup.GetPipelineStatus(ProjectPaths.ProjectRoot);
            var unvalidatedCandidate = windows.Installed ? string.Empty : UnityCliPipelineSetup.FindUnvalidatedWindowsCandidate();
            var presenceKnown = UnityCliPipelineSetup.TryGetOfficialWindowsCliPresence(out var officialCliPresent);
            var cliAbsent = presenceKnown && !officialCliPresent;
            m_windowsUnityCliStatus.text = !string.IsNullOrEmpty(unvalidatedCandidate)
                ? windows.Checked
                    ? "Detected in the official install location after a failed/older probe; refresh local CLI detection."
                    : "Detected in the official install location; local validation is pending."
                : windows.Message;
            m_windowsUnityCliPath.text = !string.IsNullOrEmpty(windows.ExecutablePath)
                ? windows.ExecutablePath
                : !string.IsNullOrEmpty(unvalidatedCandidate)
                    ? unvalidatedCandidate + " (not validated yet)"
                    : "Not available";
            m_wslUnityCliStatus.text = wsl.Message;
            m_wslUnityCliPath.text = string.IsNullOrEmpty(wsl.CommandPath) ? "Not available; default WSL distribution only is checked on explicit probe." : wsl.CommandPath;
            var resolvedWslTarget = string.Equals(wsl.Kind, "Wrapper to Windows CLI", StringComparison.Ordinal) ? wsl.TargetPath : wsl.CommandPath;
            var directMatch = wsl.ResolvesToWindowsCli
                && UnityCliPipelineSetup.WslDirectExecutableMatchesWindows(resolvedWslTarget, windows.ExecutablePath);
            m_unityCliEnvironmentMatch.text = !wsl.Checked
                ? "Unknown until explicit WSL probe."
                : directMatch
                    ? "Ready: WSL resolves the exact Windows CLI used for Pipeline."
                    : wsl.ResolvesToWindowsCli
                        ? "Bridge detected, but exact Windows CLI executable identity is unverified."
                        : "Mismatch: WSL does not reach the Windows CLI; the agent skill may use a separate environment.";
            m_pipelinePackageStatus.text = PipelinePackageStatusText(pipeline);
            m_pipelineReadiness.text = PipelineReadinessText(windows, pipeline);
            var idle = !UnityToolOperationRunning() && !UnityToolLocalRefreshRequired();
            m_installWindowsUnityCliButton.style.display = cliAbsent ? DisplayStyle.Flex : DisplayStyle.None;
            m_installWindowsUnityCliButton.SetEnabled(cliAbsent && idle && Application.platform == RuntimePlatform.WindowsEditor);
            SetActionNeeded(m_installWindowsUnityCliButton, cliAbsent && idle, "Install the optional official Windows Unity CLI after confirmation. Existing or unvalidated executables are never replaced by Install.");
            var pipelineAbsent = !pipeline.Registered && string.IsNullOrEmpty(pipeline.ManifestVersion);
            m_installPipelineButton.style.display = pipelineAbsent ? DisplayStyle.Flex : DisplayStyle.None;
            var canInstallPipeline = pipelineAbsent && idle && pipeline.EditorSupported && Application.platform == RuntimePlatform.WindowsEditor
                && windows.Installed && WindowsCliUpdateCoordinator.HasTrustedSignature(windows);
            m_installPipelineButton.SetEnabled(canInstallPipeline);
            m_installPipelineButton.tooltip = canInstallPipeline
                ? "Install the optional Pipeline package in this exact project after confirmation."
                : "Install disabled: requires Unity 6, a checked official Windows CLI with trusted signature, and no unresolved operation. Use Check CLI Updates first.";
            SetChecklist(m_pipelineReadiness, pipelineAbsent ? "optional" : pipeline.Registered ? "ok" : "info", PipelineReadinessText(windows, pipeline));
            SetChecklist(m_wslUnityCliStatus, wsl.Checked && wsl.Kind == "Not found" ? "optional" : directMatch ? "ok" : "info", wsl.Message);
            SetChecklistState(m_unityCliEnvironmentMatch, directMatch ? "ok" : "info");
            var cliTrustReady = WindowsCliUpdateCoordinator.HasTrustedSignature(windows) && !UnityToolLocalRefreshRequired();
            m_probeUnityCliButton.SetEnabled(!UnityToolOperationRunning() && !UnityToolLocalRefreshRequired());
            SetActionNeeded(m_probeUnityCliButton, false, "Refresh local Windows and WSL CLI diagnostics without installing anything.");
            var safeWrapperTarget = UnityCliPipelineSetup.IsSafeWslWrapperTarget(UnityCliPipelineSetup.WindowsPathToWsl(windows.ExecutablePath));
            var canInstallWrapper = !UnityToolOperationRunning() && windows.Installed && cliTrustReady && safeWrapperTarget && !directMatch;
            if (m_installWslWrapperButton != null) m_installWslWrapperButton.SetEnabled(canInstallWrapper);
            if (m_copyWslWrapperButton != null) m_copyWslWrapperButton.SetEnabled(windows.Installed && safeWrapperTarget);
            m_verifyPipelineButton.SetEnabled(!UnityToolOperationRunning() && windows.Installed && cliTrustReady);
            RefreshUnityToolUpdateStatus();
            RefreshSetupUnityCliSkillState();
            RefreshUnityCliProjectSkill();
        }

        void RefreshUnityCliProjectSkill()
        {
            if (m_projectUnityCliSkillStatus == null)
            {
                return;
            }

            UnityCliUpdateCheck.InvalidateIfLocalStateChanged(ProjectPaths.ProjectRoot);
            var status = UnityCliProjectSkillInstaller.GetStatus(ProjectPaths.ProjectRoot);
            var stagingReady = UnityCliProjectSkillInstaller.CanStageReviewedSkill(out var stagingError);
            m_projectUnityCliSkillStatus.text = status.Message;
            SetChecklistState(m_projectUnityCliSkillStatus, UnityCliSkillDisplayState(status, UnityCliUpdateCheck.CachedStatus.Skill.Update));
            m_projectUnityCliSkillPath.text = status.SkillPath;
            m_projectUnityCliSkillRuntime.text = stagingReady
                ? "Bounded immutable-source download ready. Node/npm is not used for official skill installation."
                : stagingError;
            if (m_updateUnityCliSkillButton != null)
            {
                var canUpdate = false;
                var updateTooltip = UnityCliSkillUpdateDisabledReason;
                if (!stagingReady)
                {
                    updateTooltip = "Update disabled: " + stagingError;
                }
                else if (UnityCliProjectSkillInstaller.HasPendingUpdateBackup(ProjectPaths.ProjectRoot))
                {
                    updateTooltip = "Update disabled: a previous skill backup exists and requires manual review.";
                }
                else if (UnityCliProjectSkillInstaller.TryCreateUpdateBinding(UnityCliUpdateCheck.CachedStatus, status, DateTime.UtcNow, out _, out var updateError))
                {
                    canUpdate = !UnityToolOperationRunning();
                    updateTooltip = "Update the exact unmodified older Unity CLI skill to the source-verified official latest artifact.";
                }
                else
                {
                    updateTooltip = "Update disabled: " + updateError;
                }

                m_updateUnityCliSkillButton.SetEnabled(canUpdate);
                m_setupUpdateUnityCliSkillButton?.SetEnabled(canUpdate);
                if (m_setupUpdateUnityCliSkillButton != null) m_setupUpdateUnityCliSkillButton.tooltip = updateTooltip;
                m_updateUnityCliSkillButton.tooltip = updateTooltip;
                SetActionNeeded(m_updateUnityCliSkillButton, canUpdate, "Update the exact source-verified older Unity CLI skill after the latest-version check.");
            }
            if (m_setupInstallUnityCliSkillButton != null)
            {
                var absent = !status.Exists && status.LocalState == UnityCliProjectSkillInstaller.LocalStateAbsent;
                var latestTargetReady = HasFreshOfficialSkillTarget(UnityCliUpdateCheck.CachedStatus.Skill.Update, DateTime.UtcNow);
                var canInstall = !UnityToolOperationRunning() && absent && stagingReady && latestTargetReady;
                m_setupInstallUnityCliSkillButton.SetEnabled(canInstall);
                m_setupInstallUnityCliSkillButton.style.display = absent ? DisplayStyle.Flex : DisplayStyle.None;
                m_installUnityCliSkillButton?.SetEnabled(canInstall);
                if (m_installUnityCliSkillButton != null) m_installUnityCliSkillButton.style.display = absent ? DisplayStyle.Flex : DisplayStyle.None;
                m_setupInstallUnityCliSkillButton.tooltip = canInstall
                    ? "Install Unity's official latest unity-cli skill into this project for OpenCode and Codex."
                    : !latestTargetReady ? "Install disabled: check the official latest skill version with Check Skill Updates first."
                    : "Install disabled: requires a safe official download transport and no running operation. " + stagingError;
                SetActionNeeded(m_setupInstallUnityCliSkillButton, canInstall, "Install Unity's official latest unity-cli skill into this project for OpenCode and Codex.");
            }
        }

        void UpdateUnityCliProjectSkill()
        {
            if (UnityToolOperationRunning())
            {
                m_lastActionLabel.text = "Another Unity CLI, Pipeline, skill, or update-check operation is already running.";
                return;
            }

            if (!UnityCliProjectSkillInstaller.CanStageReviewedSkill(out var stagingError))
            {
                m_lastActionLabel.text = "Update Unity CLI project skill: " + stagingError;
                return;
            }
            if (UnityCliProjectSkillInstaller.HasPendingUpdateBackup(ProjectPaths.ProjectRoot))
            {
                m_lastActionLabel.text = "Update Unity CLI project skill: a previous skill backup exists and requires manual review before retry.";
                return;
            }

            UnityCliUpdateCheck.InvalidateIfLocalStateChanged(ProjectPaths.ProjectRoot);
            var snapshot = UnityCliUpdateCheck.CachedStatus;
            var status = UnityCliProjectSkillInstaller.GetStatus(ProjectPaths.ProjectRoot);
            if (!UnityCliProjectSkillInstaller.TryCreateUpdateBinding(snapshot, status, DateTime.UtcNow, out var binding, out var bindingError))
            {
                m_lastActionLabel.text = "Update Unity CLI project skill: " + bindingError;
                RefreshUnityCliProjectSkill();
                RefreshSetupUnityCliSkillState();
                return;
            }

            var message = "Update the official Unity CLI agent skill in this project?\n\n"
                + "Installed commit: " + binding.InstalledReviewedCommit + "\n"
                + "Target commit: " + binding.TargetReviewedCommit + "\n"
                + "Target hash: " + binding.TargetReviewedHash + "\n"
                + "Checked: " + binding.CheckedAtUtc.ToString("u") + "\n"
                + "Source identity: " + binding.SourceIdentitySha256 + "\n"
                + "Target: " + binding.SkillRoot + "\n\n"
                + "This downloads the exact official immutable version and verifies its complete content hash, backs up the existing source-verified target, then atomically replaces it. Rollback requires exact original/replacement identities; uncertain files are preserved, never deleted. No manual content approval or SGGMcp policy change is needed.";
            if (!EditorUtility.DisplayDialog("Update Unity CLI Project Skill", message, "Update", "Cancel"))
            {
                m_lastActionLabel.text = "Update Unity CLI project skill: cancelled before mutation.";
                return;
            }

            UnityCliUpdateCheck.InvalidateIfLocalStateChanged(ProjectPaths.ProjectRoot);
            snapshot = UnityCliUpdateCheck.CachedStatus;
            status = UnityCliProjectSkillInstaller.GetStatus(ProjectPaths.ProjectRoot);
            if (!UnityCliProjectSkillInstaller.TryCreateUpdateBinding(snapshot, status, DateTime.UtcNow, out var rebound, out bindingError)
                || !string.Equals(binding.BindingIdentitySha256, rebound.BindingIdentitySha256, StringComparison.Ordinal))
            {
                m_lastActionLabel.text = "Update Unity CLI project skill: checked target or local skill content changed during confirmation. Run the manual check again.";
                RefreshUnityCliProjectSkill();
                RefreshSetupUnityCliSkillState();
                return;
            }

            if (!UnityCliProjectSkillInstaller.TryStartUpdate(ProjectPaths.ProjectRoot, rebound, out s_unityCliSkillOperation, out var error))
            {
                m_lastActionLabel.text = "Update Unity CLI project skill: " + error;
                EditorUtility.DisplayDialog("Update Unity CLI Project Skill Failed", error, "OK");
                RefreshUnityCliProjectSkill();
                RefreshSetupUnityCliSkillState();
                return;
            }

            s_unityCliSkillUpdateBinding = rebound;
            EditorApplication.update -= PollUnityCliProjectSkillInstall;
            EditorApplication.update += PollUnityCliProjectSkillInstall;
            m_lastActionLabel.text = "Unity CLI project skill update download started for official commit " + rebound.TargetReviewedCommit + ".";
            RefreshUnityCliProjectSkill();
            RefreshSetupUnityCliSkillState();
        }

        void CheckUnityToolUpdates(UnityToolUpdateComponents components)
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                m_lastActionLabel.text = "Update checking currently supports Windows Editor only.";
                return;
            }
            if (!Environment.Is64BitProcess)
            {
                m_lastActionLabel.text = "Update checking requires a 64-bit Windows Editor process.";
                return;
            }

            if (UnityToolOperationRunning())
            {
                m_lastActionLabel.text = "Another Unity CLI, Pipeline, skill, or update-check operation is already running.";
                return;
            }

            if (UnityToolLocalRefreshRequired())
            {
                m_lastActionLabel.text = "Refresh Local State before another CLI or Pipeline check after an uncertain update outcome.";
                return;
            }

            s_unityToolUpdateCheck = components == UnityToolUpdateComponents.WindowsCli
                ? UnityCliUpdateCheck.Start(ProjectPaths.ProjectRoot, components)
                : UnityCliUpdateCheck.StartAvailabilityCheck(ProjectPaths.ProjectRoot, components);
            s_unityToolUpdateCheckAutomatic = false;
            EditorApplication.update -= PollUnityToolUpdateCheck;
            EditorApplication.update += PollUnityToolUpdateCheck;
            AssemblyReloadEvents.beforeAssemblyReload -= CancelUnityToolUpdateCheck;
            AssemblyReloadEvents.beforeAssemblyReload += CancelUnityToolUpdateCheck;
            m_lastActionLabel.text = "Manual version check started for the selected component. This does not install or update anything.";
            RefreshUnityCliPipelineSetup();
        }

        void TryStartAutomaticUnityToolFreshnessCheck()
        {
            if (m_unityToolFreshnessVisibilityHandled || m_skipAutomaticRefreshForTests || m_initializingGui || SkipToolFreshnessForTests)
            {
                return;
            }

            m_unityToolFreshnessVisibilityHandled = true;
            UnityCliUpdateCheck.RestorePersistedStatus(ProjectPaths.ProjectRoot);
            if (Application.platform != RuntimePlatform.WindowsEditor || !Environment.Is64BitProcess
                || UnityToolOperationRunning() || UnityToolLocalRefreshRequired()
                || !UnityCliUpdateCheck.TryStartAutomatic(ProjectPaths.ProjectRoot, DateTime.UtcNow, out s_unityToolUpdateCheck))
            {
                RefreshUnityToolUpdateStatus();
                return;
            }

            s_unityToolUpdateCheckAutomatic = true;
            EditorApplication.update -= PollUnityToolUpdateCheck;
            EditorApplication.update += PollUnityToolUpdateCheck;
            AssemblyReloadEvents.beforeAssemblyReload -= CancelUnityToolUpdateCheck;
            AssemblyReloadEvents.beforeAssemblyReload += CancelUnityToolUpdateCheck;
            if (m_lastActionLabel != null)
            {
                m_lastActionLabel.text = "Scheduled installed-tool freshness check started for stale components only.";
            }
            RefreshUnityCliPipelineSetup();
        }

        void PollUnityToolUpdateCheck()
        {
            if (s_unityToolUpdateCheck == null || !s_unityToolUpdateCheck.Poll())
            {
                return;
            }

            EditorApplication.update -= PollUnityToolUpdateCheck;
            AssemblyReloadEvents.beforeAssemblyReload -= CancelUnityToolUpdateCheck;
            var completed = s_unityToolUpdateCheck.Status;
            s_unityToolUpdateCheck.Dispose();
            s_unityToolUpdateCheck = null;
            UnityCliUpdateCheck.RecordFreshness(ProjectPaths.ProjectRoot, completed, DateTime.UtcNow);
            var checkKind = s_unityToolUpdateCheckAutomatic ? "Scheduled" : "Manual";
            s_unityToolUpdateCheckAutomatic = false;
            m_lastActionLabel.text = completed.IsCancelled
                ? checkKind + " update check cancelled. No component was changed."
                : checkKind + " installed-tool update check completed: CLI=" + completed.WindowsCli.Update.UpdateState
                    + "; Pipeline=" + completed.Pipeline.Update.UpdateState
                    + "; Skill=" + completed.Skill.Update.UpdateState + ".";
            RefreshUnityCliPipelineSetup();
            RefreshUnityCliProjectSkill();
            ApplyLocalization();
        }

        static void CancelUnityToolUpdateCheck()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= CancelUnityToolUpdateCheck;
            if (s_unityToolUpdateCheck == null)
            {
                return;
            }

            s_unityToolUpdateCheck.Cancel();
            s_unityToolUpdateCheck.Dispose();
            s_unityToolUpdateCheck = null;
            s_unityToolUpdateCheckAutomatic = false;
        }

        void RefreshUnityToolLocalState()
        {
            if (UnityToolOperationRunning())
            {
                m_lastActionLabel.text = "Local state refresh is blocked while another CLI operation is running.";
                return;
            }

            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                m_lastActionLabel.text = "Local Windows CLI refresh is available in Windows Editor only.";
                RefreshUnityCliPipelineSetup();
                RefreshUnityCliProjectSkill();
                return;
            }

            if (!WindowsCliUpdateCoordinator.CanRefreshLocalState(out var refreshBlock)
                || !UnityPipelineUpdateOperation.CanRefreshLocalState(out refreshBlock))
            {
                m_lastActionLabel.text = "Refresh local state: " + refreshBlock;
                return;
            }
            if (!UnityPipelineUpdateOperation.TryRefreshLocalState(ProjectPaths.ProjectRoot, out var pipelineRefreshSummary))
            {
                m_lastActionLabel.text = "Refresh local Pipeline state: " + pipelineRefreshSummary;
                return;
            }

            if (!UnityCliPipelineSetup.TryStartOfficialWindowsCliProbe(out s_unityToolLocalRefreshOperation, out var error))
            {
                if (!File.Exists(UnityCliPipelineSetup.OfficialWindowsInstallPath))
                {
                    WindowsCliUpdateCoordinator.ClearLocalRefreshRequirement();
                }
                UnityCliUpdateCheck.InvalidateCache();
                m_lastActionLabel.text = "Refresh local state: " + error + " " + pipelineRefreshSummary;
                RefreshUnityCliPipelineSetup();
                RefreshUnityCliProjectSkill();
                return;
            }

            EditorApplication.update -= PollUnityToolLocalStateRefresh;
            EditorApplication.update += PollUnityToolLocalStateRefresh;
            AssemblyReloadEvents.beforeAssemblyReload -= CancelUnityToolLocalStateRefresh;
            AssemblyReloadEvents.beforeAssemblyReload += CancelUnityToolLocalStateRefresh;
            m_lastActionLabel.text = "Refreshing exact local CLI state without a network update check.";
            if (!string.IsNullOrEmpty(pipelineRefreshSummary)) m_lastActionLabel.text += " " + pipelineRefreshSummary;
            RefreshUnityCliPipelineSetup();
        }

        void PollUnityToolLocalStateRefresh()
        {
            if (s_unityToolLocalRefreshOperation == null || !s_unityToolLocalRefreshOperation.Poll())
            {
                return;
            }

            EditorApplication.update -= PollUnityToolLocalStateRefresh;
            AssemblyReloadEvents.beforeAssemblyReload -= CancelUnityToolLocalStateRefresh;
            var operation = s_unityToolLocalRefreshOperation;
            s_unityToolLocalRefreshOperation = null;
            var status = UnityCliPipelineSetup.CompleteOfficialWindowsCliProbe(operation);
            operation.Dispose();
            if (status.Checked && status.Installed)
            {
                WindowsCliUpdateCoordinator.ClearLocalRefreshRequirement();
            }
            UnityCliUpdateCheck.InvalidateCache();
            m_lastActionLabel.text = "Local CLI, Pipeline, and skill state refreshed without a network update check.";
            if (!status.Installed) m_lastActionLabel.text += " " + status.Message;
            RefreshUnityCliPipelineSetup();
            RefreshUnityCliProjectSkill();
            ApplyLocalization();
        }

        static void CancelUnityToolLocalStateRefresh()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= CancelUnityToolLocalStateRefresh;
            if (s_unityToolLocalRefreshOperation == null)
            {
                return;
            }

            s_unityToolLocalRefreshOperation.Dispose();
            s_unityToolLocalRefreshOperation = null;
            UnityCliUpdateCheck.InvalidateCache();
        }

        void CopyUnityToolUpdateDiagnostics()
        {
            UnityCliUpdateCheck.InvalidateIfLocalStateChanged(ProjectPaths.ProjectRoot);
            var snapshot = UnityCliUpdateCheck.CachedStatus;
            GUIUtility.systemCopyBuffer = "Unity tool update diagnostics\n"
                + FormatUpdateDiagnosticLine("cli", snapshot.WindowsCli.Update) + "\n"
                + FormatUpdateDiagnosticLine("pipeline", UnityPipelineUpdateOperation.EffectiveStatus(snapshot.Pipeline.Update, ProjectPaths.ProjectRoot)) + "\n"
                + FormatUpdateDiagnosticLine("skill", snapshot.Skill.Update);
            m_lastActionLabel.text = "Bounded update diagnostics copied without paths, credentials, or raw metadata.";
        }

        void RefreshUnityToolUpdateStatus()
        {
            if (m_updateCheckSummary == null)
            {
                return;
            }

            UnityCliUpdateCheck.InvalidateIfLocalStateChanged(ProjectPaths.ProjectRoot);
            var snapshot = UnityCliUpdateCheck.CachedStatus;
            var presenceKnown = UnityCliPipelineSetup.TryGetOfficialWindowsCliPresence(out var cliPresent);
            RenderUnityToolUpdateStatus(snapshot, UnityCliPipelineSetup.WindowsStatus,
                UnityCliPipelineSetup.GetPipelineStatus(ProjectPaths.ProjectRoot), UnityCliProjectSkillInstaller.GetStatus(ProjectPaths.ProjectRoot), presenceKnown, cliPresent);
        }

        internal void RenderUnityToolUpdateStatus(UnityCliUpdateCheckSnapshot snapshot,
            UnityCliPipelineSetup.WindowsUnityCliStatus windows, UnityCliPipelineSetup.UnityPipelineStatus pipeline,
            UnityCliProjectSkillInstaller.UnityCliSkillStatus skill, bool presenceKnown, bool cliPresent)
        {
            var effectivePipelineUpdate = UnityPipelineUpdateOperation.EffectiveStatus(snapshot.Pipeline.Update, ProjectPaths.ProjectRoot);
            var cliAbsent = presenceKnown && !cliPresent;
            var pipelineAbsent = !pipeline.Registered && string.IsNullOrEmpty(pipeline.ManifestVersion);
            var skillAbsent = !skill.Exists && skill.LocalState == UnityCliProjectSkillInstaller.LocalStateAbsent;
            m_windowsUnityCliUpdateStatus.text = FormatObservedUpdateStatus(snapshot.WindowsCli.Update, cliAbsent);
            m_pipelineUpdateStatus.text = FormatObservedUpdateStatus(effectivePipelineUpdate, pipelineAbsent);
            m_unityCliSkillUpdateStatus.text = FormatObservedUpdateStatus(snapshot.Skill.Update, skillAbsent);
            SetChecklist(m_pipelineAvailableVersion, UnityToolFreshnessState(pipelineAbsent, effectivePipelineUpdate), AvailableToolVersionText(effectivePipelineUpdate));
            SetChecklist(m_unityCliSkillVersion, UnityCliSkillDisplayState(skill, snapshot.Skill.Update), skillAbsent
                ? McpWindowLocalization.Text("Not installed. Optional for SGGMcp.")
                : string.IsNullOrEmpty(skill.InstalledReviewedCommit) ? McpWindowLocalization.Text("Unknown provenance; no verified skill version.") : BoundUpdateField(skill.InstalledReviewedCommit, 40));
            SetChecklist(m_unityCliSkillAvailableVersion, UnityCliSkillDisplayState(skill, snapshot.Skill.Update), AvailableToolVersionText(snapshot.Skill.Update));
            SetChecklistState(m_windowsUnityCliStatus, UnityToolFreshnessState(cliAbsent, snapshot.WindowsCli.Update));
            if (cliAbsent) m_windowsUnityCliStatus.text = McpWindowLocalization.Text("Not installed. Optional for SGGMcp.");
            SetChecklistState(m_pipelinePackageStatus, UnityToolFreshnessState(pipelineAbsent, effectivePipelineUpdate));
            SetChecklistState(m_windowsUnityCliUpdateStatus, UnityToolFreshnessState(cliAbsent, snapshot.WindowsCli.Update));
            SetChecklistState(m_pipelineUpdateStatus, UnityToolFreshnessState(pipelineAbsent, effectivePipelineUpdate));
            SetChecklistState(m_unityCliSkillUpdateStatus, UnityCliSkillDisplayState(skill, snapshot.Skill.Update));
            var noInstalledComponents = cliAbsent && pipelineAbsent && skillAbsent;
            m_updateCheckSummary.text = UnityToolLocalRefreshRequired()
                ? "An earlier CLI or Pipeline update outcome is unresolved. Use Refresh Local State before any new CLI or Pipeline action."
                : UnityToolUpdateCheckRunning()
                ? "Checking fixed official sources for the selected components. Closing the window or reloading assemblies cancels the current process."
                : noInstalledComponents
                    ? "No optional Unity tools are installed. Explicit availability checks are optional; Install remains a separate confirmed action."
                : snapshot.IsCancelled
                    ? "Last installed-tool check was cancelled. No component was changed; run it again when ready."
                    : snapshot.IsComplete
                     ? "Last component version checks are shown below. Persisted display metadata never authorizes an update; update actions require a fresh live binding."
                     : "Not checked. A stale installed component is checked once when this section is first shown; heartbeat, repaint, and generic refresh never start checks.";
            m_checkUnityToolUpdatesButton.SetEnabled(!UnityToolOperationRunning() && !UnityToolLocalRefreshRequired() && !cliAbsent);
            m_checkUnityToolUpdatesButton.tooltip = "Check the installed official Windows CLI only. This does not install or update it.";
            m_checkPipelineUpdatesButton.SetEnabled(!UnityToolOperationRunning() && !UnityToolLocalRefreshRequired());
            m_checkPipelineUpdatesButton.tooltip = "Check the official available Pipeline version, including before installation; requires a trusted Windows CLI. This never installs a package.";
            m_checkUnityCliSkillUpdatesButton.SetEnabled(!UnityToolOperationRunning() && !UnityToolLocalRefreshRequired());
            m_checkUnityCliSkillUpdatesButton.tooltip = "Check Unity's official latest skill commit, including before installation. This does not install or update anything.";
            var canUpdateWindowsCli = !UnityToolOperationRunning()
                && !UnityToolLocalRefreshRequired()
                && presenceKnown && cliPresent
                && Application.platform == RuntimePlatform.WindowsEditor
                && WindowsCliUpdateCoordinator.TryCreateBinding(snapshot, UnityCliPipelineSetup.WindowsStatus, DateTime.UtcNow, out _, out _);
            m_updateWindowsUnityCliButton.SetEnabled(canUpdateWindowsCli);
            SetActionNeeded(m_updateWindowsUnityCliButton, canUpdateWindowsCli, "Update the exact official Windows Unity CLI beta target after explicit confirmation and exact-path verification.");
            var canUpdatePipeline = !UnityToolOperationRunning()
                && !UnityToolLocalRefreshRequired()
                && UnityPipelineUpdateOperation.TryCreateBinding(
                    snapshot,
                    UnityCliPipelineSetup.GetPipelineStatus(ProjectPaths.ProjectRoot),
                    UnityCliPipelineSetup.WindowsStatus,
                    ProjectPaths.ProjectRoot,
                    ProjectPaths.ProjectRoot,
                    DateTime.UtcNow,
                    out _,
                    out _);
            m_updatePipelineButton.SetEnabled(canUpdatePipeline);
            SetActionNeeded(m_updatePipelineButton, canUpdatePipeline, "Update only the exact current project to the fresh compatible Pipeline target after explicit confirmation.");
        }

        static string FormatUpdateStatus(ComponentUpdateStatus status)
        {
            if (status != null && string.Equals(status.UpdateState, UnityCliUpdateCheck.UpdateStateNotInstalled, StringComparison.Ordinal))
            {
                return "Not installed. Optional; latest check skipped. Use Install when prerequisites are ready.";
            }
            if (status == null || status.CheckState == UnityCliUpdateCheck.CheckStateNotChecked)
            {
                return "Latest version unknown. Use this block's Check Updates button.";
            }

            var installed = string.IsNullOrEmpty(status.InstalledVersion) ? "unknown/unverified" : BoundUpdateField(status.InstalledVersion, 128);
            var available = string.IsNullOrEmpty(status.AvailableVersion) ? "unavailable" : BoundUpdateField(status.AvailableVersion, 128);
            var checkedAt = status.CheckedAtUtc == DateTime.MinValue ? "pending" : status.CheckedAtUtc.ToString("u");
            var error = string.IsNullOrEmpty(status.LastErrorCode) ? string.Empty : "; " + BoundUpdateField(status.LastErrorCode, 64) + ": " + BoundUpdateField(status.LastErrorSummary, 256);
            var summary = status.CheckState != UnityCliUpdateCheck.CheckStateChecked
                ? McpWindowLocalization.Text("Latest version unknown")
                : status.UpdateState == UnityCliUpdateCheck.UpdateStateCurrent
                    ? McpWindowLocalization.Text("Latest checked version")
                    : status.UpdateState == UnityCliUpdateCheck.UpdateStateAvailable
                        ? McpWindowLocalization.Text("Update available")
                         : McpWindowLocalization.Text("Verification required");
            return summary + "; installed=" + installed + "; available=" + available
                + "; source=" + BoundUpdateField(status.SourceKind, 64) + (string.IsNullOrEmpty(status.Channel) ? string.Empty : "/" + BoundUpdateField(status.Channel, 32))
                + "; checked=" + checkedAt + error;
        }

        internal static string FormatObservedUpdateStatus(ComponentUpdateStatus status, bool knownAbsent)
        {
            if (knownAbsent) return McpWindowLocalization.Text(status != null && status.CheckState == UnityCliUpdateCheck.CheckStateChecked
                ? "Not installed. Optional; available version is shown separately."
                : "Not installed. Optional; latest check skipped. Use Install when prerequisites are ready.");
            // Installed-only check eligibility is not an installation-presence proof.
            if (status != null && status.UpdateState == UnityCliUpdateCheck.UpdateStateNotInstalled)
                return McpWindowLocalization.Text("Installation unresolved or latest version unknown. Use this block's Check Updates button.");
            return FormatUpdateStatus(status);
        }

        internal static string AvailableToolVersionText(ComponentUpdateStatus status)
        {
            if (status == null || status.CheckState != UnityCliUpdateCheck.CheckStateChecked || string.IsNullOrEmpty(status.AvailableVersion))
                return McpWindowLocalization.Text("Latest version unknown. Use this block's Check Updates button.");
            return BoundUpdateField(status.AvailableVersion, 128)
                + "; " + McpWindowLocalization.Text("Checked") + ": " + status.CheckedAtUtc.ToString("u")
                + (status.UpdateState == UnityCliUpdateCheck.UpdateStateUpstreamReview ? "; " + McpWindowLocalization.Text("Official source metadata changed; refresh the supported immutable target before installing.") : string.Empty);
        }

        internal static bool HasFreshOfficialSkillTarget(ComponentUpdateStatus status, DateTime utcNow)
        {
            return status != null && status.CheckState == UnityCliUpdateCheck.CheckStateChecked
                && status.AvailableVersion == UnityCliProjectSkillInstaller.ReviewedSourceCommit
                && status.SourceKind == UnityCliUpdateCheck.SkillSourceKind && status.Channel == UnityCliUpdateCheck.SkillChannel
                && status.SourceIdentitySha256 == UnityCliUpdateCheck.SkillSourceIdentity(status.AvailableVersion)
                && status.CheckedAtUtc != DateTime.MinValue && status.CheckedAtUtc <= utcNow
                && status.ExpiresAtUtc > utcNow && status.ExpiresAtUtc > status.CheckedAtUtc
                && status.ExpiresAtUtc - status.CheckedAtUtc <= UnityCliUpdateCheck.CacheTtl;
        }

        static string BoundUpdateField(string value, int maxCharacters)
        {
            var safe = BoundedProcessOperation.Sanitize(value ?? string.Empty);
            return safe.Length <= maxCharacters ? safe : safe.Substring(0, maxCharacters);
        }

        static string FormatUpdateDiagnosticLine(string component, ComponentUpdateStatus status)
        {
            return component + ": " + FormatUpdateStatus(status)
                + "; source_identity_sha256=" + (status == null ? string.Empty : status.SourceIdentitySha256);
        }

        static bool UnityToolUpdateCheckRunning()
        {
            return s_unityToolUpdateCheck != null && !s_unityToolUpdateCheck.IsComplete;
        }

        static bool UnityToolOperationRunning()
        {
            return UnityToolUpdateCheckRunning() || s_unityToolLocalRefreshOperation != null || s_windowsCliProbeRunning
                || s_windowsCliInstallOperation != null || s_pipelineOperation != null || s_pipelineVerificationOperation != null
                || s_unityCliSkillOperation != null || s_wslWrapperOperation != null || s_windowsCliUpdateOperation != null
                || s_pipelineUpdateOperation != null || UnityPipelineUpdateOperation.HasActiveOperation;
        }

        static bool UnityToolLocalRefreshRequired()
        {
            return WindowsCliUpdateCoordinator.RequiresLocalRefresh || UnityPipelineUpdateOperation.RequiresLocalRefresh;
        }

        void UpdateWindowsUnityCli()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                m_lastActionLabel.text = "Windows Unity CLI update is available in Windows Editor only.";
                return;
            }

            if (UnityToolOperationRunning())
            {
                m_lastActionLabel.text = "Another Unity CLI, Pipeline, skill, or update-check operation is already running.";
                return;
            }

            UnityCliUpdateCheck.InvalidateIfLocalStateChanged(ProjectPaths.ProjectRoot);
            var snapshot = UnityCliUpdateCheck.CachedStatus;
            if (!WindowsCliUpdateCoordinator.TryCreateBinding(snapshot, UnityCliPipelineSetup.WindowsStatus, DateTime.UtcNow, out var binding, out var bindingError))
            {
                m_lastActionLabel.text = "Update Windows CLI: " + bindingError;
                RefreshUnityCliPipelineSetup();
                return;
            }

            var message = "Update the exact official Windows Unity CLI beta target?\n\n"
                + "Installed: " + binding.InstalledVersion + "\n"
                + "Target: " + binding.TargetVersion + "\n"
                + "Channel: " + binding.Channel + "\n"
                + "Checked: " + binding.CheckedAtUtc.ToString("u") + "\n"
                + "Source identity: " + binding.SourceIdentitySha256 + "\n"
                + "Executable: " + binding.ExecutablePath + "\n"
                + "Authenticode: " + binding.SignatureState + " / " + binding.Signer + "\n"
                + "Signer thumbprint: " + binding.SignatureThumbprint + "\n\n"
                + "Command: unity.exe upgrade --target " + binding.TargetVersion + " --yes --format json --non-interactive --no-banner\n\n"
                + "This runs one pinned update command. It does not authenticate, change channel, downgrade, update Pipeline, or retry after timeout. Closing the window or reloading assemblies cancels the tracked process and requires a fresh local-state check.";
            if (!EditorUtility.DisplayDialog("Update Windows Unity CLI", message, "Update", "Cancel"))
            {
                m_lastActionLabel.text = "Update Windows CLI: cancelled before mutation.";
                return;
            }

            UnityCliUpdateCheck.InvalidateIfLocalStateChanged(ProjectPaths.ProjectRoot);
            snapshot = UnityCliUpdateCheck.CachedStatus;
            if (!WindowsCliUpdateCoordinator.TryCreateBinding(snapshot, UnityCliPipelineSetup.WindowsStatus, DateTime.UtcNow, out var rebound, out bindingError)
                || !string.Equals(binding.BindingIdentitySha256, rebound.BindingIdentitySha256, StringComparison.Ordinal))
            {
                m_lastActionLabel.text = "Update Windows CLI: checked target or local state changed during confirmation. Run the manual check again.";
                RefreshUnityCliPipelineSetup();
                return;
            }

            s_windowsCliUpdateOperation = new WindowsCliUpdateCoordinator(snapshot, rebound);
            EditorApplication.update -= PollWindowsUnityCliUpdate;
            EditorApplication.update += PollWindowsUnityCliUpdate;
            AssemblyReloadEvents.beforeAssemblyReload -= CancelWindowsUnityCliUpdate;
            AssemblyReloadEvents.beforeAssemblyReload += CancelWindowsUnityCliUpdate;
            m_lastActionLabel.text = "Windows Unity CLI update preflight started for exact target " + rebound.TargetVersion + ".";
            RefreshUnityCliPipelineSetup();
            PollWindowsUnityCliUpdate();
        }

        void PollWindowsUnityCliUpdate()
        {
            if (s_windowsCliUpdateOperation == null || !s_windowsCliUpdateOperation.Poll())
            {
                return;
            }

            EditorApplication.update -= PollWindowsUnityCliUpdate;
            AssemblyReloadEvents.beforeAssemblyReload -= CancelWindowsUnityCliUpdate;
            var operation = s_windowsCliUpdateOperation;
            s_windowsCliUpdateOperation = null;
            var result = operation.Result;
            operation.Dispose();
            m_lastActionLabel.text = result.Success
                ? result.Summary
                : result.Summary + (string.IsNullOrEmpty(result.ErrorCode) ? string.Empty : " [" + result.ErrorCode + "]");
            RefreshUnityCliPipelineSetup();
            RefreshUnityCliProjectSkill();
            ApplyLocalization();
        }

        static void CancelWindowsUnityCliUpdate()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= CancelWindowsUnityCliUpdate;
            if (s_windowsCliUpdateOperation == null)
            {
                return;
            }

            s_windowsCliUpdateOperation.Cancel();
            s_windowsCliUpdateOperation.Dispose();
            s_windowsCliUpdateOperation = null;
        }

        void UpdateUnityPipeline()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                m_lastActionLabel.text = "Pipeline update is available in Windows Editor only.";
                return;
            }
            if (UnityToolOperationRunning())
            {
                m_lastActionLabel.text = "Another Unity CLI, Pipeline, skill, or update-check operation is already running.";
                return;
            }
            if (UnityToolLocalRefreshRequired())
            {
                m_lastActionLabel.text = "Refresh Local State before another Pipeline update after an unresolved operation.";
                return;
            }

            UnityCliUpdateCheck.InvalidateIfLocalStateChanged(ProjectPaths.ProjectRoot);
            var snapshot = UnityCliUpdateCheck.CachedStatus;
            var localPipeline = UnityCliPipelineSetup.GetPipelineStatus(ProjectPaths.ProjectRoot);
            if (!UnityPipelineUpdateOperation.TryCreateBinding(
                snapshot,
                localPipeline,
                UnityCliPipelineSetup.WindowsStatus,
                ProjectPaths.ProjectRoot,
                ProjectPaths.ProjectRoot,
                DateTime.UtcNow,
                out var binding,
                out var bindingError))
            {
                m_lastActionLabel.text = "Update Pipeline: " + bindingError;
                RefreshUnityCliPipelineSetup();
                return;
            }

            var message = "Update com.unity.pipeline for this exact project?\n\n"
                + "Installed: " + binding.InstalledVersion + "\n"
                + "Target: " + binding.TargetVersion + "\n"
                + "Project: " + binding.WindowsProjectRoot + "\n"
                + "Editor: " + binding.EditorVersion + "\n"
                + "Unity CLI: " + binding.CliVersion + " at " + binding.CliExecutablePath + "\n"
                + "Checked: " + binding.CheckedAtUtc.ToString("u") + "\n"
                + "Source identity: " + binding.SourceIdentitySha256 + "\n"
                + "Manifest SHA-256: " + binding.PreManifestSha256 + "\n"
                + "Expected Packages/manifest.json delta: com.unity.pipeline " + binding.InstalledVersion + " -> " + binding.TargetVersion + "\n"
                + "Expected Packages/packages-lock.json delta: "
                    + (binding.PreLockExists ? "com.unity.pipeline " + binding.PreLockVersion + " -> " + binding.TargetVersion : "missing -> resolved " + binding.TargetVersion)
                    + "\n\n"
                + "Command: unity.exe pipeline install --project-path \"" + binding.WindowsProjectRoot + "\" --package-version " + binding.TargetVersion + " --format json --non-interactive --no-banner\n\n"
                + "This performs one project-scoped pinned update, then requires exact manifest, registered Registry package, package resolution, and clean post-reload compile proof. It does not use --force, authenticate, update another project, downgrade, roll back, or replay after uncertain delivery.";
            if (!EditorUtility.DisplayDialog("Update Unity Pipeline", message, "Update", "Cancel"))
            {
                m_lastActionLabel.text = "Update Pipeline: cancelled before mutation.";
                return;
            }

            UnityCliUpdateCheck.InvalidateIfLocalStateChanged(ProjectPaths.ProjectRoot);
            snapshot = UnityCliUpdateCheck.CachedStatus;
            localPipeline = UnityCliPipelineSetup.GetPipelineStatus(ProjectPaths.ProjectRoot);
            if (!UnityPipelineUpdateOperation.TryCreateBinding(
                snapshot,
                localPipeline,
                UnityCliPipelineSetup.WindowsStatus,
                ProjectPaths.ProjectRoot,
                ProjectPaths.ProjectRoot,
                DateTime.UtcNow,
                out var rebound,
                out bindingError)
                || !string.Equals(binding.BindingIdentitySha256, rebound.BindingIdentitySha256, StringComparison.Ordinal))
            {
                m_lastActionLabel.text = "Update Pipeline: checked target, project, manifest, lock, or local package changed during confirmation. Run the manual check again.";
                RefreshUnityCliPipelineSetup();
                return;
            }

            s_pipelineUpdateOperation = new UnityPipelineUpdateCoordinator(snapshot, rebound);
            EditorApplication.update -= PollUnityPipelineUpdate;
            EditorApplication.update += PollUnityPipelineUpdate;
            AssemblyReloadEvents.beforeAssemblyReload -= CancelUnityPipelineUpdate;
            AssemblyReloadEvents.beforeAssemblyReload += CancelUnityPipelineUpdate;
            m_lastActionLabel.text = "Pipeline exact-target update started for " + rebound.TargetVersion + " in the confirmed current project.";
            RefreshUnityCliPipelineSetup();
        }

        void PollUnityPipelineUpdate()
        {
            if (s_pipelineUpdateOperation == null || !s_pipelineUpdateOperation.Poll()) return;
            EditorApplication.update -= PollUnityPipelineUpdate;
            AssemblyReloadEvents.beforeAssemblyReload -= CancelUnityPipelineUpdate;
            var operation = s_pipelineUpdateOperation;
            s_pipelineUpdateOperation = null;
            var result = operation.Result;
            operation.Dispose();
            m_lastActionLabel.text = result.Summary + (string.IsNullOrEmpty(result.ErrorCode) ? string.Empty : " [" + result.ErrorCode + "]");
            RefreshUnityCliPipelineSetup();
            ApplyLocalization();
        }

        static void CancelUnityPipelineUpdate()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= CancelUnityPipelineUpdate;
            if (s_pipelineUpdateOperation == null) return;
            s_pipelineUpdateOperation.Cancel();
            s_pipelineUpdateOperation.Dispose();
            s_pipelineUpdateOperation = null;
        }

        async void ProbeUnityCliEnvironments()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                m_lastActionLabel.text = "Windows/WSL Unity CLI probing is available in Windows Editor only.";
                return;
            }

            if (UnityToolOperationRunning())
            {
                m_lastActionLabel.text = "Windows Unity CLI detection or installation is already running.";
                return;
            }

            if (UnityToolLocalRefreshRequired())
            {
                m_lastActionLabel.text = "Use Refresh Local State before another CLI probe after an uncertain update outcome.";
                return;
            }

            s_windowsCliProbeRunning = true;
            m_lastActionLabel.text = "Checking Windows and default-WSL Unity CLI environments...";
            RefreshUnityCliPipelineSetup();
            string[] statuses;
            try
            {
                statuses = await Task.Run(() => new[] { UnityCliPipelineSetup.ProbeWindowsCli().Message, UnityCliPipelineSetup.ProbeDefaultWslCli().Message });
            }
            finally
            {
                s_windowsCliProbeRunning = false;
            }
            if (this == null || m_lastActionLabel == null)
            {
                return;
            }

            m_lastActionLabel.text = "Unity CLI probe: Windows=" + ShortActionMessage(statuses[0]) + "; WSL=" + ShortActionMessage(statuses[1]);
            RefreshUnityCliPipelineSetup();
            ApplyLocalization();
        }

        void InstallWindowsUnityCli()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                m_lastActionLabel.text = "Windows Unity CLI installation is available in Windows Editor only.";
                return;
            }

            if (UnityToolOperationRunning())
            {
                m_lastActionLabel.text = "Windows Unity CLI detection or installation is already running.";
                return;
            }

            if (UnityToolLocalRefreshRequired())
            {
                m_lastActionLabel.text = "Use Refresh Local State before another CLI install after an uncertain update outcome.";
                return;
            }

            var message = "Install the official experimental beta Windows Unity CLI?\n\n"
                + "Source: " + UnityCliPipelineSetup.WindowsCliInstallerUrl + "\n"
                + "Destination: " + UnityCliPipelineSetup.OfficialWindowsInstallPath + "\n\n"
                + "The installer downloads the latest beta from Unity's CDN, verifies the CLI binary checksum, and may add the install directory to your user PATH and write a Unity Hub install receipt. Install requires an absent target; existing or unvalidated executables must use detection and Update. It does not authenticate or install Pipeline.";
            if (!EditorUtility.DisplayDialog("Install Windows Unity CLI", message, "Install", "Cancel"))
            {
                m_lastActionLabel.text = "Install Windows Unity CLI: cancelled.";
                return;
            }

            if (!UnityCliPipelineSetup.TryStartWindowsCliInstall(out s_windowsCliInstallOperation, out var error))
            {
                m_lastActionLabel.text = "Install Windows Unity CLI: " + error;
                EditorUtility.DisplayDialog("Install Windows Unity CLI Failed", error, "OK");
                RefreshUnityCliPipelineSetup();
                return;
            }

            EditorApplication.update -= PollWindowsUnityCliInstall;
            EditorApplication.update += PollWindowsUnityCliInstall;
            AssemblyReloadEvents.beforeAssemblyReload -= CancelWindowsUnityCliInstall;
            AssemblyReloadEvents.beforeAssemblyReload += CancelWindowsUnityCliInstall;
            m_lastActionLabel.text = "Official Windows Unity CLI install started.";
            RefreshUnityCliPipelineSetup();
        }

        void PollWindowsUnityCliInstall()
        {
            if (s_windowsCliInstallOperation == null || !s_windowsCliInstallOperation.Poll())
            {
                return;
            }

            EditorApplication.update -= PollWindowsUnityCliInstall;
            AssemblyReloadEvents.beforeAssemblyReload -= CancelWindowsUnityCliInstall;
            var operation = s_windowsCliInstallOperation;
            s_windowsCliInstallOperation = null;
            var processSummary = ProcessOperationSummary(operation, "Official Windows Unity CLI installer completed.");
            var commandSucceeded = !operation.TimedOut && operation.ExitCode == 0;
            operation.Dispose();
            var status = UnityCliPipelineSetup.ProbeOfficialWindowsCli();
            UnityCliUpdateCheck.InvalidateCache();
            m_lastActionLabel.text = commandSucceeded && status.Installed
                ? "Windows Unity CLI installed and validated: " + status.Version + "."
                : processSummary + " Validation: " + status.Message;
            RefreshUnityCliPipelineSetup();
            ApplyLocalization();
        }

        static void CancelWindowsUnityCliInstall()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= CancelWindowsUnityCliInstall;
            if (s_windowsCliInstallOperation == null)
            {
                return;
            }

            s_windowsCliInstallOperation.Dispose();
            s_windowsCliInstallOperation = null;
        }

        void InstallUnityPipeline()
        {
            if (UnityToolOperationRunning())
            {
                m_lastActionLabel.text = "Another Unity CLI, Pipeline, skill, or update-check operation is already running.";
                return;
            }

            if (UnityToolLocalRefreshRequired() || !WindowsCliUpdateCoordinator.HasTrustedSignature(UnityCliPipelineSetup.WindowsStatus))
            {
                m_lastActionLabel.text = "Pipeline install requires a fresh trusted exact Windows CLI check and no unresolved CLI update.";
                return;
            }

            var root = Path.GetFullPath(ProjectPaths.ProjectRoot);
            var message = "Install optional package '" + UnityCliPipelineSetup.PipelinePackageId + "' through the official Windows Unity CLI?\n\n"
                + "Command: unity.exe pipeline install --project-path \"" + root + "\"\n\n"
                + "This is a network and project package manifest operation. SGGMcp does not require Pipeline.";
            if (!EditorUtility.DisplayDialog("Install Unity Pipeline", message, "Install", "Cancel"))
            {
                m_lastActionLabel.text = "Install Unity Pipeline: cancelled.";
                return;
            }

            if (!UnityCliPipelineSetup.TryStartPipelineInstall(root, out s_pipelineOperation, out var error))
            {
                m_lastActionLabel.text = "Install Unity Pipeline: " + error;
                EditorUtility.DisplayDialog("Install Unity Pipeline Failed", error, "OK");
                RefreshUnityCliPipelineSetup();
                return;
            }

            EditorApplication.update -= PollUnityPipelineInstall;
            EditorApplication.update += PollUnityPipelineInstall;
            m_lastActionLabel.text = "Unity Pipeline install started for the current project.";
            RefreshUnityCliPipelineSetup();
        }

        void PollUnityPipelineInstall()
        {
            if (s_pipelineOperation == null || !s_pipelineOperation.Poll())
            {
                return;
            }

            EditorApplication.update -= PollUnityPipelineInstall;
            var operation = s_pipelineOperation;
            s_pipelineOperation = null;
            var summary = ProcessOperationSummary(operation, "Unity Pipeline install command completed. Wait for Package Manager resolution and compilation.");
            operation.Dispose();
            m_lastActionLabel.text = summary;
            RefreshUnityCliPipelineSetup();
            ApplyLocalization();
        }

        void VerifyUnityPipeline()
        {
            if (UnityToolOperationRunning())
            {
                m_lastActionLabel.text = "Pipeline verification or another update/local-state operation is already running.";
                return;
            }

            if (UnityToolLocalRefreshRequired() || !WindowsCliUpdateCoordinator.HasTrustedSignature(UnityCliPipelineSetup.WindowsStatus))
            {
                m_lastActionLabel.text = "Pipeline verification requires a fresh trusted exact Windows CLI check and no unresolved CLI update.";
                return;
            }

            if (!UnityCliPipelineSetup.TryStartPipelineVerification(ProjectPaths.ProjectRoot, out s_pipelineVerificationOperation, out var error))
            {
                m_lastActionLabel.text = "Verify Pipeline: " + error;
                return;
            }

            EditorApplication.update -= PollUnityPipelineVerification;
            EditorApplication.update += PollUnityPipelineVerification;
            m_pipelineConnectionStatus.text = "Checking official Pipeline connection for the current project.";
        }

        void PollUnityPipelineVerification()
        {
            if (s_pipelineVerificationOperation == null || !s_pipelineVerificationOperation.Poll())
            {
                return;
            }

            EditorApplication.update -= PollUnityPipelineVerification;
            var operation = s_pipelineVerificationOperation;
            s_pipelineVerificationOperation = null;
            if (!operation.TimedOut && operation.ExitCode == 0 && UnityCliPipelineSetup.PipelineListContainsProject(operation.StandardOutput, ProjectPaths.ProjectRoot))
            {
                m_pipelineConnectionStatus.text = "Ready: official Unity Pipeline connection verified for the current project.";
            }
            else if (!operation.TimedOut && operation.ExitCode == 0)
            {
                m_pipelineConnectionStatus.text = "Verification failed: Pipeline list did not contain the exact current project path.";
            }
            else
            {
                m_pipelineConnectionStatus.text = ProcessOperationSummary(operation, "Ready: official Unity Pipeline connection verified for the current project.");
            }
            operation.Dispose();
            ApplyLocalization();
        }

        void InstallUnityCliProjectSkill()
        {
            if (UnityToolOperationRunning())
            {
                m_lastActionLabel.text = "Another Unity CLI, Pipeline, skill, or update-check operation is already running.";
                return;
            }

            if (!HasFreshOfficialSkillTarget(UnityCliUpdateCheck.CachedStatus.Skill.Update, DateTime.UtcNow))
            {
                m_lastActionLabel.text = "Check the official latest skill version with Check Skill Updates before installation.";
                return;
            }

            var target = Path.Combine(Path.GetFullPath(ProjectPaths.ProjectRoot), ".agents", "skills", UnityCliProjectSkillInstaller.SkillName);
            var message = "Install Unity's official latest Unity CLI agent skill into this project?\n\n"
                + "Source: " + UnityCliProjectSkillInstaller.ReviewedSource + "\nOfficial commit: " + UnityCliProjectSkillInstaller.ReviewedSourceCommit + "\nTarget: " + target + "\nAgents: OpenCode, Codex\nMode: immutable download, exact content hash verification, then project copy\n\n"
                + "This network/write action makes instructions with allowed-tools: Bash discoverable to new agent sessions. It does not change SGGMcp policy.";
            if (!EditorUtility.DisplayDialog("Install Unity CLI Project Skill", message, "Install", "Cancel"))
            {
                m_lastActionLabel.text = "Install Unity CLI project skill: cancelled.";
                return;
            }

            if (!HasFreshOfficialSkillTarget(UnityCliUpdateCheck.CachedStatus.Skill.Update, DateTime.UtcNow))
            {
                m_lastActionLabel.text = "The checked official latest skill target expired or changed during confirmation; check again before installation.";
                return;
            }

            if (!UnityCliProjectSkillInstaller.TryStartInstall(ProjectPaths.ProjectRoot, out s_unityCliSkillOperation, out var error))
            {
                m_lastActionLabel.text = "Install Unity CLI project skill: " + error;
                EditorUtility.DisplayDialog("Install Unity CLI Project Skill Failed", error, "OK");
                RefreshUnityCliProjectSkill();
                RefreshSetupUnityCliSkillState();
                return;
            }

            s_unityCliSkillUpdateBinding = null;
            EditorApplication.update -= PollUnityCliProjectSkillInstall;
            EditorApplication.update += PollUnityCliProjectSkillInstall;
            m_lastActionLabel.text = "Unity CLI project skill install started.";
            RefreshUnityCliProjectSkill();
            RefreshSetupUnityCliSkillState();
        }

        void InstallWslUnityCliWrapper()
        {
            if (UnityToolOperationRunning())
            {
                m_lastActionLabel.text = "Another Unity CLI, Pipeline, skill, or update-check operation is already running.";
                return;
            }

            if (UnityToolLocalRefreshRequired() || !WindowsCliUpdateCoordinator.HasTrustedSignature(UnityCliPipelineSetup.WindowsStatus))
            {
                m_lastActionLabel.text = "WSL wrapper install requires a fresh trusted exact Windows CLI check and no unresolved CLI update.";
                return;
            }

            var windows = UnityCliPipelineSetup.WindowsStatus;
            var wslTarget = UnityCliPipelineSetup.WindowsPathToWsl(windows.ExecutablePath);
            var message = "Install a Unity CLI wrapper in the default WSL distribution?\n\n"
                + "Target: ~/.local/bin/unity\nWindows CLI: " + windows.ExecutablePath + "\nWSL target: " + wslTarget + "\n\n"
                + "An existing file or symlink is never overwritten. The wrapper only forwards arguments to the validated Windows Unity CLI.";
            if (!EditorUtility.DisplayDialog("Install WSL Unity CLI Wrapper", message, "Install", "Cancel"))
            {
                m_lastActionLabel.text = "Install WSL Unity CLI wrapper: cancelled.";
                return;
            }

            if (!UnityCliPipelineSetup.TryStartWslWrapperInstall(out s_wslWrapperOperation, out var error))
            {
                m_lastActionLabel.text = "Install WSL Unity CLI wrapper: " + error;
                EditorUtility.DisplayDialog("Install WSL Unity CLI Wrapper Failed", error, "OK");
                return;
            }

            EditorApplication.update -= PollWslUnityCliWrapperInstall;
            EditorApplication.update += PollWslUnityCliWrapperInstall;
            m_lastActionLabel.text = "Installing ~/.local/bin/unity in the default WSL distribution.";
            RefreshUnityCliPipelineSetup();
        }

        void PollWslUnityCliWrapperInstall()
        {
            if (s_wslWrapperOperation == null || !s_wslWrapperOperation.Poll())
            {
                return;
            }

            EditorApplication.update -= PollWslUnityCliWrapperInstall;
            var operation = s_wslWrapperOperation;
            s_wslWrapperOperation = null;
            var succeeded = !operation.TimedOut && operation.ExitCode == 0;
            m_lastActionLabel.text = ProcessOperationSummary(operation, "WSL Unity CLI wrapper installed. Rechecking the default WSL distribution...");
            operation.Dispose();
            RefreshUnityCliPipelineSetup();
            if (succeeded) RefreshWslAfterWrapperInstall();
        }

        async void RefreshWslAfterWrapperInstall()
        {
            await Task.Run(UnityCliPipelineSetup.ProbeDefaultWslCli);
            if (this == null || m_lastActionLabel == null) return;
            var status = UnityCliPipelineSetup.WslStatus;
            var resolvedTarget = string.Equals(status.Kind, "Wrapper to Windows CLI", StringComparison.Ordinal) ? status.TargetPath : status.CommandPath;
            var exactMatch = status.ResolvesToWindowsCli && UnityCliPipelineSetup.WslDirectExecutableMatchesWindows(resolvedTarget, UnityCliPipelineSetup.WindowsStatus.ExecutablePath);
            m_lastActionLabel.text = exactMatch
                ? "WSL Unity CLI wrapper installed and validated." + (status.OnPath ? string.Empty : " Restart the WSL/agent session so ~/.local/bin is loaded on PATH.")
                : "WSL wrapper was installed, but another command shadows it or exact Windows CLI identity was not verified.";
            RefreshUnityCliPipelineSetup();
            ApplyLocalization();
        }

        void PollUnityCliProjectSkillInstall()
        {
            if (s_unityCliSkillOperation == null || !s_unityCliSkillOperation.Poll())
            {
                return;
            }

            EditorApplication.update -= PollUnityCliProjectSkillInstall;
            var operation = s_unityCliSkillOperation;
            s_unityCliSkillOperation = null;
            var processSucceeded = !operation.TimedOut && operation.ExitCode == 0;
            var processSummary = ProcessOperationSummary(operation, "Unity CLI project skill download completed.");
            operation.Dispose();
            if (processSucceeded)
            {
                var updateBinding = s_unityCliSkillUpdateBinding;
                s_unityCliSkillUpdateBinding = null;
                if (updateBinding != null)
                {
                    UnityCliProjectSkillInstaller.TryFinalizeUpdate(ProjectPaths.ProjectRoot, updateBinding, out var finalizeMessage);
                    m_lastActionLabel.text = processSummary + " " + finalizeMessage;
                }
                else
                {
                    UnityCliProjectSkillInstaller.TryFinalizeInstall(ProjectPaths.ProjectRoot, out var finalizeMessage);
                    m_lastActionLabel.text = processSummary + " " + finalizeMessage;
                }
            }
            else if (s_unityCliSkillUpdateBinding != null)
            {
                s_unityCliSkillUpdateBinding = null;
                m_lastActionLabel.text = processSummary + " Nothing was changed in .agents/skills.";
            }
            else
            {
                m_lastActionLabel.text = processSummary + " Nothing was copied into .agents/skills.";
            }
            RefreshUnityCliProjectSkill();
            RefreshSetupUnityCliSkillState();
            ApplyLocalization();
        }

        void OpenUnityCliProjectSkillFolder()
        {
            var status = UnityCliProjectSkillInstaller.GetStatus(ProjectPaths.ProjectRoot);
            if (!Directory.Exists(status.SkillRoot))
            {
                m_lastActionLabel.text = "Project unity-cli skill folder does not exist: " + status.SkillRoot;
                return;
            }

            EditorUtility.RevealInFinder(status.SkillRoot);
            m_lastActionLabel.text = "Project unity-cli skill folder opened.";
        }

        void CopyUnityPipelineCommand()
        {
            GUIUtility.systemCopyBuffer = "unity pipeline install --project-path \"" + Path.GetFullPath(ProjectPaths.ProjectRoot) + "\"";
            m_lastActionLabel.text = "Unity Pipeline install command copied.";
        }

        void CopyWslUnityCliWrapperSetup()
        {
            var target = UnityCliPipelineSetup.WindowsPathToWsl(UnityCliPipelineSetup.WindowsStatus.ExecutablePath);
            GUIUtility.systemCopyBuffer = "set -eu\ntarget=~/.local/bin/unity\nif [ -e \"$target\" ] || [ -L \"$target\" ]; then echo 'Not overwriting existing ~/.local/bin/unity' >&2; exit 23; fi\nmkdir -p ~/.local/bin\numask 077\ntmp=\"$target.sgg-tmp-$$\"\nprintf '%s\\n' '#!/usr/bin/env bash' 'exec \"" + target + "\" \"$@\"' > \"$tmp\"\nchmod 700 \"$tmp\"\nmv \"$tmp\" \"$target\"\nhash -r\nunity --version";
            m_lastActionLabel.text = "WSL wrapper setup copied. Review it before changing your user shell environment.";
        }

        void CopyUnityCliProjectSkillCommand()
        {
            GUIUtility.systemCopyBuffer = UnityCliProjectSkillInstaller.ReviewedSource;
            m_lastActionLabel.text = "Immutable official Unity CLI skill source copied. Use the confirmed Install action for verified project copy.";
        }

        string PipelinePackageStatusText(UnityCliPipelineSetup.UnityPipelineStatus status)
        {
            if (status.Registered)
            {
                return "Installed: " + status.Version + " from " + status.Source + ".";
            }

            return string.IsNullOrEmpty(status.ManifestVersion)
                ? "Not installed. Optional for SGGMcp."
                : "Declared in Packages/manifest.json: " + status.ManifestVersion + ". Unity may still be resolving/importing it.";
        }

        string PipelineReadinessText(UnityCliPipelineSetup.WindowsUnityCliStatus windows, UnityCliPipelineSetup.UnityPipelineStatus pipeline)
        {
            if (pipeline.Registered) return "Ready: package is registered. Verify official connection separately.";
            if (!string.IsNullOrEmpty(pipeline.ManifestVersion)) return "Resolving/importing: package is already declared in Packages/manifest.json.";
            if (s_pipelineOperation != null) return "Installing through the official Windows Unity CLI.";
            if (s_windowsCliInstallOperation != null) return "Installing the official Windows Unity CLI.";
            return "Not installed. Automatic freshness checks skip this optional package; use its check button to query available versions.";
        }

        string NextActionState(UnityCliPipelineSetup.WindowsUnityCliStatus windows, UnityCliPipelineSetup.UnityPipelineStatus pipeline)
        {
            if (!pipeline.EditorSupported) return "error";
            if (!windows.Installed) return "warn";
            if (pipeline.Registered) return "ok";
            return string.IsNullOrEmpty(pipeline.ManifestVersion) ? "ok" : "warn";
        }

        string NextActionText(UnityCliPipelineSetup.WindowsUnityCliStatus windows, UnityCliPipelineSetup.UnityPipelineStatus pipeline, string unvalidatedCandidate)
        {
            if (!pipeline.EditorSupported) return "Blocked - Pipeline requires Unity 6.0 or newer.";
            if (s_windowsCliInstallOperation != null) return "Installing - wait for the Windows Unity CLI installer and validation.";
            if (s_pipelineOperation != null) return "Installing - wait for the CLI command, Package Manager import, and compilation.";
            if (!windows.Installed && !string.IsNullOrEmpty(unvalidatedCandidate)) return "Next action - click 1. Detect/Validate CLI. Unity CLI was found at the official Windows install path but this Editor inherited an older PATH.";
            if (!windows.Checked) return "Next action - click 1. Detect/Validate CLI.";
            if (!windows.Installed) return "Next action - click Install Windows CLI, then wait for automatic validation.";
            if (pipeline.Registered) return "Ready - Pipeline package is installed. Optionally verify the official connection.";
            if (!string.IsNullOrEmpty(pipeline.ManifestVersion)) return "Next action - wait for Package Manager resolution and compilation, then refresh.";
            return "Next action - click 2. Install Pipeline.";
        }

        string PipelineInstallStateText(UnityCliPipelineSetup.WindowsUnityCliStatus windows, UnityCliPipelineSetup.UnityPipelineStatus pipeline)
        {
            if (!pipeline.EditorSupported) return "Blocked - current Unity version is below 6.0.";
            if (pipeline.Registered) return "Ready - com.unity.pipeline " + pipeline.Version + " is already installed.";
            if (!string.IsNullOrEmpty(pipeline.ManifestVersion)) return "Blocked - com.unity.pipeline is already declared and still resolving/importing.";
            if (s_pipelineOperation != null) return "Installing - wait for the CLI command and Package Manager import.";
            if (!windows.Checked) return "Blocked - first click 1. Detect/Validate CLI.";
            if (!windows.Installed) return "Blocked - Windows Unity CLI was not validated. The WSL/Linux CLI cannot install Pipeline into Windows Editor.";
            return "Ready - click 2. Install Pipeline.";
        }

        void RefreshSetupUnityCliSkillState()
        {
            if (m_setupUnityCliSkillState == null)
            {
                return;
            }

            var status = UnityCliProjectSkillInstaller.GetStatus(ProjectPaths.ProjectRoot);
            if (s_unityCliSkillOperation != null)
            {
                SetChecklist(m_setupUnityCliSkillState, "info", s_unityCliSkillUpdateBinding != null
                    ? "Updating - downloading and validating the official Unity CLI skill. Previous content is backed up; identity-verified rollback preserves failed replacements."
                    : "Installing - downloading and validating the official Unity CLI skill. Nothing is copied into .agents/skills until validation passes.");
                return;
            }

            if (status.Valid)
            {
                SetChecklist(m_setupUnityCliSkillState,
                    UnityCliSkillDisplayState(status, UnityCliUpdateCheck.CachedStatus.Skill.Update),
                    string.Equals(status.LocalState, UnityCliProjectSkillInstaller.LocalStatePreviousReviewed, StringComparison.Ordinal)
                        ? "Update available - an older source-verified unity-cli skill is installed; check the official latest version before replacing it."
                        : "Ready - source-verified official unity-cli skill is installed at .agents/skills/unity-cli.");
                return;
            }

            if (status.Exists)
            {
                SetChecklist(m_setupUnityCliSkillState, "info", "Installed unmanaged - existing unity-cli skill has unknown provenance or invalid content; open Skill Details for manual review.");
                return;
            }
            SetChecklist(m_setupUnityCliSkillState, "optional", "Not installed - freshness checks skip this optional project skill.");
        }

        NodeRuntimeInstaller.NodeRuntimeStatus CurrentNodeRuntimeStatus()
        {
            return m_lastNodeRuntimeStatus ?? NodeRuntimeInstaller.GetStatus();
        }

        string ProcessOperationSummary(BoundedProcessOperation operation, string successMessage)
        {
            if (operation.TimedOut)
            {
                return "Outcome unknown after timeout. Refresh actual project state; the action will not retry automatically.";
            }

            if (operation.ExitCode == 0)
            {
                return successMessage;
            }

            var output = string.IsNullOrWhiteSpace(operation.StandardError) ? operation.StandardOutput : operation.StandardError;
            output = McpDiagnosticsCopyViewModel.Redact(Bounded(output, 600));
            return "Command failed with exit code " + operation.ExitCode + (string.IsNullOrEmpty(output) ? "." : ": " + output);
        }

        void ResumeUnityCliOperations()
        {
            if (s_pipelineOperation != null) EditorApplication.update += PollUnityPipelineInstall;
            if (s_pipelineUpdateOperation != null) EditorApplication.update += PollUnityPipelineUpdate;
            if (s_pipelineVerificationOperation != null) EditorApplication.update += PollUnityPipelineVerification;
            if (s_unityCliSkillOperation != null) EditorApplication.update += PollUnityCliProjectSkillInstall;
            if (s_wslWrapperOperation != null) EditorApplication.update += PollWslUnityCliWrapperInstall;
        }
    }
}
