using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Commands;
using SGG.UnityMCP.Editor.Setup;
using SGG.UnityMCP.Editor.UI.Localization;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace SGG.UnityMCP.Editor.UI
{
    public sealed partial class UnityMcpSetupWindow
    {
        TextField PolicyPatternsField(VisualElement parent, string key)
        {
            var field = new TextField(key) { multiline = true };
            field.AddToClassList("mcp-input-row");
            field.AddToClassList("mcp-input-field");
            field.AddToClassList("mcp-policy-editor-field");
            field.RegisterValueChangedCallback(_ => OnPolicyEditorDraftChanged());
            parent.Add(field);
            return field;
        }

        void CopySelectedCommandId()
        {
            var command = SelectedCommand();
            CopyText("Command Id", command == null ? string.Empty : command.Id);
        }

        void CopySelectedCommandArgsExample()
        {
            var command = SelectedCommand();
            CopyText("args_json template", command == null ? "{}" : ExampleArgsJson(command.SchemaJson));
        }

        void CopySelectedCommandSchema()
        {
            var command = SelectedCommand();
            CopyText("Schema", command == null ? string.Empty : command.SchemaJson);
        }

        void CopySelectedCommandPolicyExplainArgsJson()
        {
            var command = SelectedCommand();
            CopyText("policy.explain args_json", command == null ? "{}" : PolicyExplainArgsJson(command));
        }

        void CopySelectedCommandEffectivePolicySummary()
        {
            var command = SelectedCommand();
            if (command == null)
            {
                CopyText("Effective Policy Summary", "No command selected.");
                return;
            }

            var featureStatus = FeatureFlagStatus(command.FeatureFlag);
            var policyDecision = PolicyDecisionSummary(command);
            CopyText("Effective Policy Summary",
                "command: " + command.Id + "\n"
                + "risk/idempotency: " + command.Risk + " / " + command.Idempotency + "\n"
                + "feature_flag: " + EmptyAsNone(command.FeatureFlag) + "\n"
                + "feature_status: " + featureStatus + "\n"
                + "policy_decision: " + policyDecision + "\n"
                + "policy_draft_exact_change: " + CommandPolicyDraftExactChangeSummary(command) + "\n"
                + "gateway_visibility: " + GatewayVisibility(command, featureStatus, policyDecision) + "\n"
                + "human_result: " + HumanCommandResult(command, featureStatus, policyDecision) + "\n"
                + "why_unavailable: " + WhyUnavailable(command, featureStatus, policyDecision) + "\n"
                + "caveat: local regex preview only; Gateway policy.explain is authoritative when the Gateway is running. This copy helper does not execute the selected command.");
        }

        void CopySelectedCommandCallSnippet()
        {
            var command = SelectedCommand();
            if (command == null)
            {
                CopyText("sgg_unity_call snippet", string.Empty);
                return;
            }

            var argsJson = ExampleArgsJson(command.SchemaJson);
            CopyText("sgg_unity_call snippet", "sgg_unity_call(project=\"" + ProjectPaths.ProjectId + "\", command=\"" + command.Id + "\", args_json=\"" + EscapeSnippetString(argsJson) + "\", mode=\"wait\")");
        }

        void ConfirmApplyProfile(string profile, string title)
        {
            ConfirmApplyProfile(profile, title, false);
        }

        void ConfirmApplyProfile(string profile, string title, bool enableFlags)
        {
            var message = "Apply the '" + title + "' permission profile to project '" + ProjectPaths.ProjectId + "'?\n\n"
                + "This edits the user policy file in LocalAppData. Profiles only change policy and reload live; feature-gated commands still require matching feature flags in Gateway config. Refresh Health after policy-only changes. Restart after other config or feature changes; only saved approvals.auto_allow_known_unity_popups can apply live on a supported runtime.";
            if (enableFlags)
            {
                message += "\n\nThis action also enables the matching feature flags in Gateway config.";
            }

            if (!EditorUtility.DisplayDialog("Apply Policy Profile", message, "Apply", "Cancel"))
            {
                m_lastActionLabel.text = "Apply " + title + ": cancelled.";
                return;
            }

            RunAction(enableFlags ? "Apply " + title + " + Enable Flags" : "Apply " + title, () => ApplyProfileWithFtueEvidence(profile, title, enableFlags), !enableFlags);
        }

        void ConfirmEnableAllFeatureFlags()
        {
            var message = "Enable all known Gateway feature flags?\n\n"
                + "This makes every implemented command family visible after Gateway restart. Policy still controls allow/ask/deny unless you also apply Allow All.";
            if (!EditorUtility.DisplayDialog("Enable All Feature Flags", message, "Enable All", "Cancel"))
            {
                m_lastActionLabel.text = "Enable All Feature Flags: cancelled.";
                return;
            }

            RunAction("Enable All Feature Flags", GatewayPolicyManager.EnableAllFeatureFlags);
        }

        void ConfirmAllowAll(bool enableAllFlags)
        {
            var message = "DANGER: Allow all built-in/trusted Gateway-visible commands for project '" + ProjectPaths.ProjectId + "'?\n\n"
                + "This removes ask gates for write, execute, destructive and network-risk commands in the local policy. Untrusted extension packages remain trust-gated.";
            if (enableAllFlags)
            {
                message += "\n\nThis action also enables every known feature flag in Gateway config.";
            }

            if (!EditorUtility.DisplayDialog("Danger: Allow All", message, enableAllFlags ? "Enable All + Allow All" : "Allow All", "Cancel"))
            {
                m_lastActionLabel.text = (enableAllFlags ? "Enable All + Allow All" : "Apply Allow All") + ": cancelled.";
                return;
            }

            RunAction(enableAllFlags ? "Enable All + Allow All" : "Apply Allow All", () => ApplyProfileWithFtueEvidence(GatewayPolicyManager.AllowAllProfile, "Allow All", enableAllFlags), !enableAllFlags);
        }

        NodeRuntimeInstaller.InstallResult ApplyProfileWithFtueEvidence(string profile, string title, bool enableFlags)
        {
            var result = enableFlags
                ? profile == GatewayPolicyManager.AllowAllProfile ? EnableAllAndApplyAllowAll() : ApplyProfileAndEnableFlags(profile)
                : GatewayPolicyManager.ApplyProfile(profile);
            if (!result.Success)
            {
                return result;
            }

            var status = GatewayPolicyManager.GetStatus();
            var rules = ReadProjectPolicyRules(status.PolicyPath, status.ProjectId);
            var fingerprint = McpFtueProfileTracker.RulesFingerprint(rules.Allow, rules.Ask, rules.Deny);
            McpFtueProfileTracker.RecordExplicitProfile(ProjectPaths.ProjectRoot, title, fingerprint);
            return result;
        }

        NodeRuntimeInstaller.InstallResult ApplyProfileAndEnableFlags(string profile)
        {
            var flags = GatewayPolicyManager.EnableFeatureProfile(profile);
            if (!flags.Success)
            {
                return flags;
            }

            var policy = GatewayPolicyManager.ApplyProfile(profile);
            if (!policy.Success)
            {
                return policy;
            }

            return NodeRuntimeInstaller.InstallResult.Ok(policy.Message + " " + flags.Message);
        }

        NodeRuntimeInstaller.InstallResult EnableAllAndApplyAllowAll()
        {
            var flags = GatewayPolicyManager.EnableAllFeatureFlags();
            if (!flags.Success)
            {
                return flags;
            }

            var policy = GatewayPolicyManager.ApplyProfile(GatewayPolicyManager.AllowAllProfile);
            if (!policy.Success)
            {
                return policy;
            }

            return NodeRuntimeInstaller.InstallResult.Ok(policy.Message + " " + flags.Message);
        }

        void AddBaselinePermissions()
        {
            ConfirmApplyProfile(GatewayPolicyManager.ReadonlyProfile, "Readonly");
        }

        void RefreshPermissions()
        {
            if (m_permissionsStatus == null)
            {
                return;
            }

            var status = GatewayPolicyManager.GetStatus();
            m_permissionsStatus.text = status.Message;
            m_permissionsProject.text = status.ProjectId;
            m_permissionsConfigPath.text = status.ConfigPath;
            m_permissionsPolicyPath.text = status.PolicyPath;
            m_permissionsProjectPolicy.text = status.ProjectPolicyReady ? "Present for project '" + status.ProjectId + "'" : "Missing for project '" + status.ProjectId + "' - create config/policy files or apply a profile, then Refresh Health.";
            m_permissionsFeatureHint.text = "Feature flags live in Gateway config; policy/permissions do not enable them. Open Gateway Config to edit flags, then restart Gateway.";
            RefreshProjectPolicyRules(status);
            RefreshProjectPolicyEditor(status, false);
            RefreshCatalogPolicySummary();
            var featureFlagsSummary = ReadFeatureFlagsSummary(status.ConfigPath);
            if (m_permissionsFeatureFlagsSummary != null)
            {
                m_permissionsFeatureFlagsSummary.text = featureFlagsSummary;
            }

            if (m_permissionsAutoAllowAsk != null)
            {
                m_permissionsAutoAllowAsk.SetValueWithoutNotify(GatewayPolicyManager.GetAutoAllowAsk());
            }

            if (m_permissionsAutoAllowKnownUnityPopups != null)
            {
                m_permissionsAutoAllowKnownUnityPopups.SetValueWithoutNotify(GatewayPolicyManager.GetAutoAllowKnownUnityPopups());
            }

            m_permissionsApprovalsDir.text = ReadApprovalsDir(status.ConfigPath);
            m_permissionsPendingApprovalsList.text = ReadPendingApprovalsSummary(m_permissionsApprovalsDir.text, status.ProjectId);
            m_permissionsApprovalsList.text = ReadApprovalsSummary(m_permissionsApprovalsDir.text, status.ProjectId);
            if (m_statusPolicyProfile != null)
            {
                m_statusPolicyProfile.text = status.UserConfigReady ? status.ProjectPolicyReady ? "User policy/config present for this project" : "User policy/config present; project policy missing" : "User policy/config missing";
            }

            if (m_statusFeatureFlags != null)
            {
                m_statusFeatureFlags.text = featureFlagsSummary + "; configured in Gateway config.";
            }

            if (m_statusUnityPackageVersion != null)
            {
                m_statusUnityPackageVersion.text = ReadUnityPackageVersion();
                m_statusCanonicalPathVersion.text = CanonicalPathDependencyStatusText();
                m_statusMcpToken.text = McpTokenStatus();
            }

            UpdatePermissionsPreview(SelectedCommand());

            SetIndicator(m_permissionsIndicator, status.UserConfigReady && status.ProjectPolicyReady, status.RuntimeReady && (!status.UserConfigReady || !status.ProjectPolicyReady));
        }

        bool PermissionsBaselineActionNeeded(GatewayPolicyManager.PolicyStatus policyStatus)
        {
            return policyStatus != null
                && policyStatus.RuntimeReady
                && (!policyStatus.UserConfigReady || !policyStatus.ProjectPolicyReady);
        }

        bool PermissionsAllowAllCompletionNeeded(GatewayPolicyManager.PolicyStatus policyStatus)
        {
            if (policyStatus == null || !policyStatus.RuntimeReady)
            {
                return false;
            }

            var allFlagsEnabled = AllKnownFeatureFlagsEnabled(policyStatus.ConfigPath);
            var projectAllowsAll = ProjectPolicyAllowsAll(policyStatus.PolicyPath, policyStatus.ProjectId);
            return allFlagsEnabled != projectAllowsAll;
        }

        bool AllKnownFeatureFlagsEnabled(string configPath)
        {
            if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
            {
                return false;
            }

            var source = File.ReadAllText(configPath);
            var flags = GatewayPolicyManager.AllFeatureFlags;
            for (var index = 0; index < flags.Length; index++)
            {
                var pattern = "\\\"" + Regex.Escape(flags[index]) + "\\\"\\s*:\\s*true";
                if (!Regex.IsMatch(source, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(250)))
                {
                    return false;
                }
            }

            return true;
        }

        bool ProjectPolicyAllowsAll(string policyPath, string projectId)
        {
            if (string.IsNullOrEmpty(policyPath) || string.IsNullOrEmpty(projectId) || !File.Exists(policyPath))
            {
                return false;
            }

            var source = File.ReadAllText(policyPath);
            var projectSource = ProjectPolicySource(source, projectId);
            if (string.IsNullOrEmpty(projectSource))
            {
                return false;
            }

            var allow = PolicySectionValues(projectSource, "allow");
            var ask = PolicySectionValues(projectSource, "ask");
            var deny = PolicySectionValues(projectSource, "deny");
            return allow.Contains("*") && ask.Count == 0 && deny.Count == 0;
        }

        void RefreshCommandsAndLocalize()
        {
            RefreshCommands();
            ApplyLocalization();
        }

        void RefreshPermissionsAndLocalize()
        {
            RefreshPermissions();
            ApplyLocalization();
        }

        void RefreshProjectPolicyRules(GatewayPolicyManager.PolicyStatus status)
        {
            if (m_permissionsPolicyRulesSummary == null)
            {
                return;
            }

            var snapshot = ReadProjectPolicyRulesSnapshot(status.PolicyPath, status.ProjectId);
            m_permissionsPolicyRulesSummary.text = snapshot.Summary;
            m_permissionsPolicyRulesAllow.text = snapshot.Allow;
            m_permissionsPolicyRulesAsk.text = snapshot.Ask;
            m_permissionsPolicyRulesDeny.text = snapshot.Deny;
            m_permissionsPolicyRulesDefaults.text = snapshot.Defaults;
        }

        void CopyPolicyRulesSummary()
        {
            var status = GatewayPolicyManager.GetStatus();
            var snapshot = ReadProjectPolicyRulesSnapshot(status.PolicyPath, status.ProjectId);
            CopyText("Project Policy Rules Summary",
                "Project: " + status.ProjectId + "\n"
                + "Policy file: " + status.PolicyPath + "\n"
                + "Saved project rules: " + snapshot.Summary + "\n"
                + "Allow: " + snapshot.Allow + "\n"
                + "Ask: " + snapshot.Ask + "\n"
                + "Deny: " + snapshot.Deny + "\n"
                + "Risk defaults: " + snapshot.Defaults + "\n"
                + "Note: read-only snapshot; this UI slice does not create approvals, consume approvals or execute commands.");
        }

        void RefreshCatalogPolicySummary()
        {
            if (m_permissionsCatalogPolicySummary == null)
            {
                return;
            }

            m_permissionsCatalogPolicySummary.text = CatalogPolicySummaryText(out _);
        }

        void CopyCatalogPolicySummary()
        {
            CopyText("Catalog Policy Summary", StripRichText(CatalogPolicySummaryText(out _)));
        }

        void RefreshProjectPolicyEditor(GatewayPolicyManager.PolicyStatus status, bool forceReload)
        {
            if (m_policyEditorAllowField == null)
            {
                return;
            }

            var sameSource = string.Equals(m_policyEditorLoadedProjectId, status.ProjectId, StringComparison.Ordinal)
                && string.Equals(m_policyEditorLoadedPolicyPath, status.PolicyPath, StringComparison.Ordinal);
            if (!forceReload && sameSource && ProjectPolicyEditorDirty())
            {
                UpdatePolicyEditorDraftStatus();
                return;
            }

            var snapshot = ReadProjectPolicyRules(status.PolicyPath, status.ProjectId);
            m_policyEditorRefreshing = true;
            m_policyEditorAllowField.SetValueWithoutNotify(PolicyEditorText(snapshot.Allow));
            m_policyEditorAskField.SetValueWithoutNotify(PolicyEditorText(snapshot.Ask));
            m_policyEditorDenyField.SetValueWithoutNotify(PolicyEditorText(snapshot.Deny));
            m_policyEditorRefreshing = false;
            m_policyEditorLoadedProjectId = status.ProjectId;
            m_policyEditorLoadedPolicyPath = status.PolicyPath;
            m_policyEditorSavedAllow = m_policyEditorAllowField.value ?? string.Empty;
            m_policyEditorSavedAsk = m_policyEditorAskField.value ?? string.Empty;
            m_policyEditorSavedDeny = m_policyEditorDenyField.value ?? string.Empty;
            m_policyEditorStatus.text = snapshot.EditorStatus;
            m_policyEditorReview.text = "No unsaved changes. Edit one command id or wildcard per line, then Review Draft before saving.";
            if (m_policyEditorExactChanges != null)
            {
                m_policyEditorExactChanges.text = "No exact command changes. The Commands policy action filter 'draft_changed' compares exact command ids only; wildcard effects stay in Review Draft warnings.";
            }
        }

        void ReloadProjectPolicyDraft()
        {
            RefreshProjectPolicyEditor(GatewayPolicyManager.GetStatus(), true);
            OnPolicyEditorDraftChanged();
            m_lastActionLabel.text = "Reloaded saved project policy draft.";
        }

        void ResetProjectPolicyDraft()
        {
            if (m_policyEditorAllowField == null)
            {
                return;
            }

            m_policyEditorRefreshing = true;
            m_policyEditorAllowField.SetValueWithoutNotify(m_policyEditorSavedAllow);
            m_policyEditorAskField.SetValueWithoutNotify(m_policyEditorSavedAsk);
            m_policyEditorDenyField.SetValueWithoutNotify(m_policyEditorSavedDeny);
            m_policyEditorRefreshing = false;
            OnPolicyEditorDraftChanged();
            m_lastActionLabel.text = "Reset project policy draft to the last loaded saved values.";
        }

        void ReviewProjectPolicyDraft()
        {
            if (!TryReadProjectPolicyDraft(out var allow, out var ask, out var deny, out var error))
            {
                m_policyEditorReview.text = error;
                EditorUtility.DisplayDialog("Project Policy Draft Invalid", error, "OK");
                return;
            }

            var review = ProjectPolicyChangeReview(allow, ask, deny);
            m_policyEditorReview.text = review;
            if (m_policyEditorExactChanges != null)
            {
                m_policyEditorExactChanges.text = ProjectPolicyExactDraftChangesSummary(allow, ask, deny);
            }
            m_lastActionLabel.text = "Reviewed project policy draft.";
        }

        void RefreshPolicyEditorCommandChoices(IReadOnlyList<CommandDescriptor> descriptors)
        {
            if (m_policyEditorCommandField == null)
            {
                return;
            }

            var previous = m_policyEditorCommandField.value;
            var choices = new List<string>();
            if (descriptors != null)
            {
                for (var index = 0; index < descriptors.Count; index++)
                {
                    if (!string.IsNullOrEmpty(descriptors[index].Id))
                    {
                        choices.Add(descriptors[index].Id);
                    }
                }
            }

            if (choices.Count == 0)
            {
                choices.Add("catalog unavailable");
            }

            m_policyEditorCommandField.choices = choices;
            var selectedIndex = choices.IndexOf(previous);
            m_policyEditorCommandField.SetValueWithoutNotify(selectedIndex >= 0 ? previous : choices[0]);
        }

        void UpdatePolicyEditorActionState()
        {
            if (m_policyEditorActionField == null)
            {
                return;
            }

            var action = m_policyEditorActionField.value;
            ResetPolicyActionClasses(m_policyEditorActionField);
            m_policyEditorActionField.AddToClassList(PolicyActionClass(action));
            if (m_policyEditorActionPreview != null)
            {
                m_policyEditorActionPreview.text = PolicyActionBadge(action) + " exact command rules will be drafted into the " + action + " bucket; Save Project Policy still requires explicit confirmation.";
            }
        }

        void ResetPolicyActionClasses(VisualElement element)
        {
            element.RemoveFromClassList("mcp-policy-action--allow");
            element.RemoveFromClassList("mcp-policy-action--ask");
            element.RemoveFromClassList("mcp-policy-action--deny");
            element.RemoveFromClassList("mcp-policy-action--none");
            element.RemoveFromClassList("mcp-policy-action--unknown");
        }

        string PolicyActionClass(string action)
        {
            if (action == "allow")
            {
                return "mcp-policy-action--allow";
            }

            if (action == "ask")
            {
                return "mcp-policy-action--ask";
            }

            if (action == "deny")
            {
                return "mcp-policy-action--deny";
            }

            if (action == "none")
            {
                return "mcp-policy-action--none";
            }

            return "mcp-policy-action--unknown";
        }

        string PolicyActionBadge(string action)
        {
            if (action == "allow")
            {
                return "[ALLOW]";
            }

            if (action == "ask")
            {
                return "[ASK]";
            }

            if (action == "deny")
            {
                return "[DENY]";
            }

            if (action == "none")
            {
                return "[NONE]";
            }

            return "[UNKNOWN]";
        }

        void AddPolicyEditorDropdownRule()
        {
            var commandId = m_policyEditorCommandField == null ? string.Empty : m_policyEditorCommandField.value;
            if (string.IsNullOrEmpty(commandId) || commandId == "catalog unavailable")
            {
                EditorUtility.DisplayDialog("Draft Policy Rule", "No catalog command is available. Refresh the Gateway/runtime command catalog first.", "OK");
                return;
            }

            var targetSection = m_policyEditorActionField == null ? "ask" : m_policyEditorActionField.value;
            if (targetSection != "allow" && targetSection != "ask" && targetSection != "deny")
            {
                targetSection = "ask";
            }

            if (!TryReadProjectPolicyDraft(out var allow, out var ask, out var deny, out var error))
            {
                m_policyEditorReview.text = error;
                EditorUtility.DisplayDialog("Project Policy Draft Invalid", error, "OK");
                return;
            }

            MoveExactPolicyPatternToSection(commandId, targetSection, allow, ask, deny);
            m_policyEditorRefreshing = true;
            m_policyEditorAllowField.SetValueWithoutNotify(PolicyEditorText(allow));
            m_policyEditorAskField.SetValueWithoutNotify(PolicyEditorText(ask));
            m_policyEditorDenyField.SetValueWithoutNotify(PolicyEditorText(deny));
            m_policyEditorRefreshing = false;
            m_selectedCommandId = commandId;
            OnPolicyEditorDraftChanged();
            m_policyEditorReview.text = ProjectPolicyChangeReview(allow, ask, deny);
            m_lastActionLabel.text = "Added catalog command '" + commandId + "' to draft " + targetSection + " rules. Review and Save Project Policy to write policy.json.";
        }

        void AddSelectedCommandToPolicyDraft(string targetSection)
        {
            var command = SelectedCommand();
            if (command == null)
            {
                EditorUtility.DisplayDialog("Draft Policy Rule", "No command selected in the Commands catalog.", "OK");
                return;
            }

            if (!TryReadProjectPolicyDraft(out var allow, out var ask, out var deny, out var error))
            {
                m_policyEditorReview.text = error;
                EditorUtility.DisplayDialog("Project Policy Draft Invalid", error, "OK");
                return;
            }

            MoveExactPolicyPatternToSection(command.Id, targetSection, allow, ask, deny);
            m_policyEditorRefreshing = true;
            m_policyEditorAllowField.SetValueWithoutNotify(PolicyEditorText(allow));
            m_policyEditorAskField.SetValueWithoutNotify(PolicyEditorText(ask));
            m_policyEditorDenyField.SetValueWithoutNotify(PolicyEditorText(deny));
            m_policyEditorRefreshing = false;
            OnPolicyEditorDraftChanged();
            m_policyEditorReview.text = ProjectPolicyChangeReview(allow, ask, deny);
            m_lastActionLabel.text = "Added selected command '" + command.Id + "' to draft " + targetSection + " rules. Review and Save Project Policy to write policy.json.";
        }

        void MoveExactPolicyPatternToSection(string pattern, string targetSection, List<string> allow, List<string> ask, List<string> deny)
        {
            RemoveExactPolicyPattern(allow, pattern);
            RemoveExactPolicyPattern(ask, pattern);
            RemoveExactPolicyPattern(deny, pattern);
            if (targetSection == "allow")
            {
                allow.Add(pattern);
            }
            else if (targetSection == "ask")
            {
                ask.Add(pattern);
            }
            else
            {
                deny.Add(pattern);
            }
        }

        void RemoveExactPolicyPattern(List<string> values, string pattern)
        {
            for (var index = values.Count - 1; index >= 0; index--)
            {
                if (string.Equals(values[index], pattern, StringComparison.Ordinal))
                {
                    values.RemoveAt(index);
                }
            }
        }

        void SaveProjectPolicyDraft()
        {
            if (!TryReadProjectPolicyDraft(out var allow, out var ask, out var deny, out var error))
            {
                m_policyEditorReview.text = error;
                EditorUtility.DisplayDialog("Project Policy Draft Invalid", error, "OK");
                return;
            }

            var review = ProjectPolicyChangeReview(allow, ask, deny);
            m_policyEditorReview.text = review;
            if (!ProjectPolicyEditorDirty())
            {
                EditorUtility.DisplayDialog("Save Project Policy", "No changes to save for project '" + GatewayPolicyManager.GetStatus().ProjectId + "'.", "OK");
                return;
            }

            if (!EditorUtility.DisplayDialog("Save Project Policy", review + "\n\nThis writes only the current Project Id allow/ask/deny lists in local policy.json. The running Gateway reloads a valid replacement live; Refresh Health after saving.", "Save", "Cancel"))
            {
                return;
            }

            var result = SaveProjectPolicyRules(GatewayPolicyManager.GetStatus(), allow, ask, deny);
            m_lastActionLabel.text = "Save Project Policy: " + result.Message;
            if (!result.Success)
            {
                EditorUtility.DisplayDialog("Save Project Policy Failed", result.Message, "OK");
                return;
            }

            RefreshProjectPolicyEditor(GatewayPolicyManager.GetStatus(), true);
            RefreshProcess(true);
            RefreshPermissionsAndLocalize();
            RefreshCommands();
        }

        void UpdatePolicyEditorDraftStatus()
        {
            if (m_policyEditorRefreshing || m_policyEditorStatus == null)
            {
                return;
            }

            if (!TryReadProjectPolicyDraft(out var allow, out var ask, out var deny, out var error))
            {
                m_policyEditorStatus.text = "Invalid draft - " + error;
                m_policyEditorReview.text = error;
                if (m_policyEditorExactChanges != null)
                {
                    m_policyEditorExactChanges.text = "Invalid draft - exact command changes unavailable until validation passes.";
                }
                return;
            }

            m_policyEditorStatus.text = (ProjectPolicyEditorDirty() ? "Unsaved changes" : "No unsaved changes")
                + "; allow " + allow.Count + ", ask " + ask.Count + ", deny " + deny.Count
                + ". Save requires explicit confirmation; policy reload is live and should be verified with Refresh Health.";
            if (m_policyEditorExactChanges != null)
            {
                m_policyEditorExactChanges.text = ProjectPolicyExactDraftChangesSummary(allow, ask, deny);
            }
        }

        void OnPolicyEditorDraftChanged()
        {
            UpdatePolicyEditorDraftStatus();
            if (m_commandsPolicyFilter != null && string.Equals(m_commandsPolicyFilter.value, "draft_changed", StringComparison.Ordinal))
            {
                RefreshCommands();
                return;
            }

            UpdateCommandDetails(SelectedCommand());
        }

        bool TryReadProjectPolicyDraft(out List<string> allow, out List<string> ask, out List<string> deny, out string error)
        {
            allow = PolicyEditorPatterns(m_policyEditorAllowField == null ? string.Empty : m_policyEditorAllowField.value, "allow", out error);
            if (!string.IsNullOrEmpty(error))
            {
                ask = new List<string>();
                deny = new List<string>();
                return false;
            }

            ask = PolicyEditorPatterns(m_policyEditorAskField == null ? string.Empty : m_policyEditorAskField.value, "ask", out error);
            if (!string.IsNullOrEmpty(error))
            {
                deny = new List<string>();
                return false;
            }

            deny = PolicyEditorPatterns(m_policyEditorDenyField == null ? string.Empty : m_policyEditorDenyField.value, "deny", out error);
            if (!string.IsNullOrEmpty(error))
            {
                return false;
            }

            error = DuplicatePolicyPatternAcrossSections(allow, ask, deny);
            return string.IsNullOrEmpty(error);
        }

        bool ProjectPolicyEditorDirty()
        {
            return !string.Equals(m_policyEditorAllowField == null ? string.Empty : m_policyEditorAllowField.value ?? string.Empty, m_policyEditorSavedAllow, StringComparison.Ordinal)
                || !string.Equals(m_policyEditorAskField == null ? string.Empty : m_policyEditorAskField.value ?? string.Empty, m_policyEditorSavedAsk, StringComparison.Ordinal)
                || !string.Equals(m_policyEditorDenyField == null ? string.Empty : m_policyEditorDenyField.value ?? string.Empty, m_policyEditorSavedDeny, StringComparison.Ordinal);
        }

        void SetAutoAllowAsk(bool enabled)
        {
            if (enabled && !EditorUtility.DisplayDialog("Enable Temporary Ask Bypass", "This edits approvals.auto_allow_ask=true in the local user Gateway config. It only auto-allows policy action ask; deny, feature flags, schema validation and PathGuard are unchanged. Disable it once native client prompts are available.", "Enable", "Cancel"))
            {
                m_permissionsAutoAllowAsk.SetValueWithoutNotify(false);
                return;
            }

            var result = GatewayPolicyManager.SetAutoAllowAsk(enabled);
            m_lastActionLabel.text = "Auto-allow ask: " + result.Message;
            if (!result.Success)
            {
                EditorUtility.DisplayDialog("Auto-allow ask Failed", result.Message, "OK");
            }

            RefreshPermissionsAndLocalize();
        }

        void SetAutoAllowKnownUnityPopups(bool enabled)
        {
            var russian = McpWindowLocalization.CurrentLanguage == "ru";
            var title = russian ? "Разрешить ответы на известные окна Unity" : "Enable Known Unity Popup Replies";
            if (enabled && !GatewayPolicyManager.GetAutoAllowKnownUnityPopups())
            {
                var warning = russian
                    ? "Разрешить агенту выбирать поддерживаемые ответы на известные окна Unity без повторного подтверждения? Это может отменить несохранённые изменения, перезагрузить сцену или подтвердить понижение версии проекта.\n\nИзменяется только approvals.auto_allow_known_unity_popups. DENY, флаги функций, точные проверки владельца/окна/процесса и одноразовое выполнение без автоматического повтора сохраняются. auto_allow_ask не изменяется. В Gateway runtime с поддержкой live-reload этой настройки сохранённое включение/отключение действует со следующего запроса без перезапуска; перед отправкой ответа настройка проверяется снова. Действующее состояние смотрите в diagnostics.status popup_response_approval.enabled."
                    : "Allow agent-chosen supported replies to known Unity popups without repeat confirmation? This can discard unsaved changes, reload a scene or accept a project downgrade.\n\nOnly approvals.auto_allow_known_unity_popups is changed. DENY, feature flags, exact owner/window/process checks and one-shot execution with no automatic replay remain. auto_allow_ask is unchanged. On a Gateway runtime supporting live popup settings, saved enable/disable changes apply on the next request without restart and are rechecked before response issuance. Check diagnostics.status popup_response_approval.enabled for the running state.";
                if (!EditorUtility.DisplayDialog(title, warning, russian ? "Включить" : "Enable", russian ? "Отмена" : "Cancel"))
                {
                    m_permissionsAutoAllowKnownUnityPopups.SetValueWithoutNotify(false);
                    return;
                }
            }

            var result = GatewayPolicyManager.SetAutoAllowKnownUnityPopups(enabled);
            m_lastActionLabel.text = title + ": " + result.Message;
            if (!result.Success)
            {
                EditorUtility.DisplayDialog(title, result.Message, "OK");
            }

            RefreshPermissionsAndLocalize();
        }

        void RefreshCommands()
        {
            if (m_commandsCatalogSummary == null)
            {
                return;
            }

            UnityBridgeRuntime.RefreshCommandCatalog("MCP Window command refresh");
            var descriptors = UnityBridgeRuntime.CommandDescriptors;
            RefreshPolicyEditorCommandChoices(descriptors);
            var group = m_commandsGroupFilter == null ? "all" : m_commandsGroupFilter.value;
            var risk = m_commandsRiskFilter == null ? "all" : m_commandsRiskFilter.value;
            var feature = m_commandsFeatureFilter == null ? "all" : m_commandsFeatureFilter.value;
            var policy = m_commandsPolicyFilter == null ? "all" : m_commandsPolicyFilter.value;
            var search = m_commandsSearchField == null ? string.Empty : m_commandsSearchField.value;
            m_commandsShown.Clear();
            foreach (var descriptor in descriptors)
            {
                if (MatchesCommandFilters(descriptor, group, risk, feature, policy, search))
                {
                    m_commandsShown.Add(descriptor);
                }
            }

            if (m_commandsShown.Count > 0 && !ContainsCommandId(m_commandsShown, m_selectedCommandId))
            {
                m_selectedCommandId = m_commandsShown[0].Id;
            }

            if (m_commandsShown.Count == 0)
            {
                m_selectedCommandId = string.Empty;
            }

            m_commandsCatalogSummary.text = m_commandsShown.Count + " of " + descriptors.Count + " commands; hash " + (string.IsNullOrEmpty(UnityBridgeRuntime.CommandsHash) ? "not available" : UnityBridgeRuntime.CommandsHash);
            m_commandsList.itemsSource = m_commandsShown;
            m_commandsList.Rebuild();
            var selectedIndex = IndexOfCommandId(m_commandsShown, m_selectedCommandId);
            if (selectedIndex >= 0)
            {
                m_commandsList.selectedIndex = selectedIndex;
            }
            UpdateCommandDetails(selectedIndex < 0 ? null : m_commandsShown[selectedIndex]);
        }

        void OnCommandSelectionChanged(IEnumerable<object> selectedItems)
        {
            foreach (var item in selectedItems)
            {
                var command = item as CommandDescriptor;
                if (command != null)
                {
                    m_selectedCommandId = command.Id;
                    UpdateCommandDetails(command);
                    ApplyLocalization();
                    return;
                }
            }
        }

        string CommandListLabel(CommandDescriptor command)
        {
            var featureStatus = FeatureFlagStatus(command.FeatureFlag);
            var policyDecision = PolicyDecisionSummary(command);
            return command.Id
                + "  |  risk=" + command.Risk
                + "  |  policy=" + PolicyActionBucket(policyDecision)
                + "  |  feature=" + FeatureStatusBucket(featureStatus);
        }

        void BindCommandListItem(Label label, CommandDescriptor command)
        {
            ResetCommandRowClasses(label);
            label.AddToClassList("mcp-command-row");
            label.AddToClassList(CommandRowStateClass(command));
            label.text = CommandListLabel(command);
            label.tooltip = CommandAvailability(command);
        }

        void ResetCommandRowClasses(VisualElement element)
        {
            element.RemoveFromClassList("mcp-command-row--allow");
            element.RemoveFromClassList("mcp-command-row--ask");
            element.RemoveFromClassList("mcp-command-row--deny");
            element.RemoveFromClassList("mcp-command-row--unknown");
            element.RemoveFromClassList("mcp-command-row--feature-hidden");
        }

        string CommandRowStateClass(CommandDescriptor command)
        {
            var featureStatus = FeatureFlagStatus(command.FeatureFlag);
            if (featureStatus.StartsWith("disabled:", StringComparison.Ordinal) || featureStatus.StartsWith("missing:", StringComparison.Ordinal))
            {
                return "mcp-command-row--feature-hidden";
            }

            var policy = PolicyActionBucket(PolicyDecisionSummary(command));
            if (policy == "allow")
            {
                return "mcp-command-row--allow";
            }

            if (policy == "ask")
            {
                return "mcp-command-row--ask";
            }

            if (policy == "deny")
            {
                return "mcp-command-row--deny";
            }

            return "mcp-command-row--unknown";
        }

        internal static string PolicyActionBucket(string decision)
        {
            if (decision.StartsWith("allow:", StringComparison.Ordinal) || decision.StartsWith("default allow", StringComparison.Ordinal))
            {
                return "allow";
            }

            if (decision.StartsWith("ask:", StringComparison.Ordinal) || decision.StartsWith("default ask", StringComparison.Ordinal))
            {
                return "ask";
            }

            if (decision.StartsWith("deny:", StringComparison.Ordinal) || decision.StartsWith("default deny", StringComparison.Ordinal))
            {
                return "deny";
            }

            return "unknown";
        }

        string FeatureStatusBucket(string featureStatus)
        {
            if (featureStatus.StartsWith("disabled:", StringComparison.Ordinal) || featureStatus.StartsWith("missing:", StringComparison.Ordinal))
            {
                return "feature_hidden";
            }

            if (featureStatus.StartsWith("enabled:", StringComparison.Ordinal))
            {
                return "enabled";
            }

            return string.IsNullOrEmpty(featureStatus) ? "unknown" : featureStatus;
        }

        bool MatchesCommandFilters(CommandDescriptor descriptor, string group, string risk, string feature, string policy, string search)
        {
            if (!MatchesCommandSearchAndRiskFilters(descriptor, risk, search))
            {
                return false;
            }

            if (feature == "none" && !string.IsNullOrEmpty(descriptor.FeatureFlag))
            {
                return false;
            }

            if (feature == "flagged" && string.IsNullOrEmpty(descriptor.FeatureFlag))
            {
                return false;
            }

            if (!MatchesPolicyActionFilter(descriptor, policy))
            {
                return false;
            }

            return MatchesCommandGroupFilter(descriptor, group);
        }

        internal static bool MatchesCommandSearchAndRiskFilters(CommandDescriptor descriptor, string risk, string search)
        {
            if (descriptor == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(search)
                && (descriptor.Id ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0
                && (descriptor.Title ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0
                && (descriptor.Description ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            if (risk != "all" && descriptor.Risk != risk)
            {
                return false;
            }

            return true;
        }

        internal static bool MatchesCommandGroupFilter(CommandDescriptor descriptor, string group)
        {
            if (descriptor == null)
            {
                return false;
            }

            if (group == "all")
            {
                return true;
            }

            var id = descriptor.Id ?? string.Empty;
            var capability = descriptor.Capability ?? string.Empty;
            if (id.StartsWith(group + ".", StringComparison.Ordinal) || capability == group || capability.StartsWith(group + "_", StringComparison.Ordinal))
            {
                return true;
            }

            foreach (var tag in descriptor.Tags ?? new string[0])
            {
                if (tag == group)
                {
                    return true;
                }
            }

            return false;
        }

        bool MatchesPolicyActionFilter(CommandDescriptor descriptor, string policy)
        {
            if (policy == "all")
            {
                return true;
            }

            var featureStatus = FeatureFlagStatus(descriptor.FeatureFlag);
            if (policy == "feature_hidden")
            {
                return featureStatus.StartsWith("disabled:", StringComparison.Ordinal) || featureStatus.StartsWith("missing:", StringComparison.Ordinal);
            }

            if (policy == "draft_changed")
            {
                return CommandPolicyDraftExactChanged(descriptor);
            }

            var decision = PolicyDecisionSummary(descriptor);
            if (policy == "allow")
            {
                return decision.StartsWith("allow:", StringComparison.Ordinal) || decision.StartsWith("default allow", StringComparison.Ordinal);
            }

            if (policy == "ask")
            {
                return decision.StartsWith("ask:", StringComparison.Ordinal) || decision.StartsWith("default ask", StringComparison.Ordinal);
            }

            if (policy == "deny")
            {
                return decision.StartsWith("deny:", StringComparison.Ordinal) || decision.StartsWith("default deny", StringComparison.Ordinal);
            }

            return !decision.StartsWith("allow:", StringComparison.Ordinal)
                && !decision.StartsWith("default allow", StringComparison.Ordinal)
                && !decision.StartsWith("ask:", StringComparison.Ordinal)
                && !decision.StartsWith("default ask", StringComparison.Ordinal)
                && !decision.StartsWith("deny:", StringComparison.Ordinal)
                && !decision.StartsWith("default deny", StringComparison.Ordinal);
        }

        int IndexOfCommandId(IReadOnlyList<CommandDescriptor> descriptors, string commandId)
        {
            for (var index = 0; index < descriptors.Count; index++)
            {
                if (descriptors[index].Id == commandId)
                {
                    return index;
                }
            }

            return -1;
        }

        bool ContainsCommandId(IReadOnlyList<CommandDescriptor> descriptors, string commandId)
        {
            return IndexOfCommandId(descriptors, commandId) >= 0;
        }

        CommandDescriptor SelectedCommand()
        {
            var index = IndexOfCommandId(m_commandsShown, m_selectedCommandId);
            return index < 0 ? null : m_commandsShown[index];
        }

        void UpdateCommandDetails(CommandDescriptor command)
        {
            if (m_commandsPreview == null)
            {
                return;
            }

            if (command == null)
            {
                m_commandsStatusBadges.text = "No command selected";
                m_commandsPreview.text = "No commands match current filters";
                m_commandsAvailability.text = "No command selected";
                m_commandsArgsExample.text = "{}";
                UpdatePermissionsPreview(null);
                return;
            }

            m_commandsStatusBadges.text = CommandStatusBadges(command);
            m_commandsPreview.text = CommandPreview(command);
            m_commandsAvailability.text = CommandAvailability(command);
            m_commandsArgsExample.text = ExampleArgsJson(command.SchemaJson);
            UpdatePermissionsPreview(command);
        }

        string CommandStatusBadges(CommandDescriptor command)
        {
            var featureStatus = FeatureFlagStatus(command.FeatureFlag);
            var policyDecision = PolicyDecisionSummary(command);
            return "feature=" + featureStatus
                + " | policy=" + policyDecision
                + " | exact_draft_change=" + CommandPolicyDraftExactChangeSummary(command)
                + " | visibility=" + GatewayVisibility(command, featureStatus, policyDecision);
        }

        void UpdatePermissionsPreview(CommandDescriptor command)
        {
            if (m_permissionsPreviewRisk == null)
            {
                return;
            }

            if (command == null)
            {
                m_permissionsPreviewRisk.text = "No command selected";
                m_permissionsPreviewFeatureFlag.text = "No command selected";
                m_permissionsPreviewDecision.text = "No command selected";
                m_permissionsPreviewVisibility.text = "No command selected";
                m_permissionsPreviewHumanResult.text = "No command selected";
                m_permissionsPreviewCaveat.text = "Gateway policy.explain remains authoritative when the Gateway is running.";
                return;
            }

            var featureStatus = FeatureFlagStatus(command.FeatureFlag);
            var policyDecision = PolicyDecisionSummary(command);
            m_permissionsPreviewRisk.text = command.Risk + " / " + command.Idempotency;
            m_permissionsPreviewFeatureFlag.text = featureStatus;
            m_permissionsPreviewDecision.text = policyDecision;
            m_permissionsPreviewVisibility.text = GatewayVisibility(command, featureStatus, policyDecision);
            m_permissionsPreviewHumanResult.text = HumanCommandResult(command, featureStatus, policyDecision);
            m_permissionsPreviewCaveat.text = "Local regex preview can miss project-specific JSON edge cases; Gateway policy.explain is authoritative.";
        }

        string CommandPreview(CommandDescriptor command)
        {
            var availability = command.ResultShape == "handler_unavailable" ? "\navailability: handler_unavailable - catalog/manifest entry exists, but the handler is unavailable" : string.Empty;
            return "id: " + command.Id
                + "\ntitle: " + command.Title
                + "\ndescription: " + command.Description
                + "\npackage/source/trust: " + command.Package + " / " + EmptyAsNone(command.Source) + " / " + EmptyAsNone(command.Trust)
                + "\nrisk/idempotency/timeout_ms: " + command.Risk + " / " + command.Idempotency + " / " + command.TimeoutMs
                + "\nschema: " + Bounded(command.SchemaJson, 700)
                + "\ntags/capability/requires_editor_status: " + string.Join(",", command.Tags) + " / " + EmptyAsNone(command.Capability) + " / " + string.Join(",", command.RequiresEditorStatus)
                + "\nresult_shape/output_limit: " + EmptyAsNone(command.ResultShape) + " / " + Bounded(EmptyAsNone(command.OutputLimitJson), 200)
                + "\nsupports_job/supports_cancel: " + command.SupportsJob + " / " + command.SupportsCancel
                + "\nfeature_flag: " + EmptyAsNone(command.FeatureFlag)
                + "\npolicy_decision: " + PolicyDecisionSummary(command)
                + "\npolicy_draft_exact_change: " + CommandPolicyDraftExactChangeSummary(command)
                + "\ngateway_visibility: " + GatewayVisibility(command, FeatureFlagStatus(command.FeatureFlag), PolicyDecisionSummary(command))
                + "\nwhy_unavailable: " + WhyUnavailable(command, FeatureFlagStatus(command.FeatureFlag), PolicyDecisionSummary(command))
                + "\nargs_json_example: " + ExampleArgsJson(command.SchemaJson)
                + availability;
        }

        string CommandAvailability(CommandDescriptor command)
        {
            var featureStatus = FeatureFlagStatus(command.FeatureFlag);
            var policyDecision = PolicyDecisionSummary(command);
            return "feature: " + featureStatus
                + "; policy: " + policyDecision
                + "; exact_draft_change: " + CommandPolicyDraftExactChangeSummary(command)
                + "; visibility: " + GatewayVisibility(command, featureStatus, policyDecision)
                + "; human_result: " + HumanCommandResult(command, featureStatus, policyDecision)
                + "; why_unavailable: " + WhyUnavailable(command, featureStatus, policyDecision);
        }

        string HumanCommandResult(CommandDescriptor command, string featureStatus, string policyDecision)
        {
            if (featureStatus.StartsWith("disabled:", StringComparison.Ordinal) || featureStatus.StartsWith("missing:", StringComparison.Ordinal))
            {
                return "Hidden because the matching feature flag is disabled or missing; enable it in Gateway config, restart/refresh, and remember policy alone cannot show it.";
            }

            if (policyDecision.StartsWith("deny:", StringComparison.Ordinal) || policyDecision.StartsWith("default deny", StringComparison.Ordinal))
            {
                return "Blocked by current Project Id policy; apply the smallest matching profile or edit policy, then Refresh Health to verify live reload. Do not bypass policy.";
            }

            if (policyDecision.StartsWith("ask:", StringComparison.Ordinal) || policyDecision.StartsWith("default ask", StringComparison.Ordinal))
            {
                return "Visible, but execution needs human approval outside MCP; this UI cannot approve or consume approvals.";
            }

            if (command.ResultShape == "handler_unavailable")
            {
                return "Visible because the catalog/manifest entry exists, but the handler is unavailable; feature or policy changes alone may not fix it.";
            }

            return "Visible and allowed by local preview.";
        }

        string FeatureFlagStatus(string featureFlag)
        {
            return FeatureFlagStatus(featureFlag, ReadPolicyPreviewContext());
        }

        string FeatureFlagStatus(string featureFlag, PolicyPreviewContext context)
        {
            if (string.IsNullOrEmpty(featureFlag))
            {
                return "not feature-gated";
            }

            if (context == null || string.IsNullOrEmpty(context.Status.ConfigPath) || string.IsNullOrEmpty(context.ConfigSource))
            {
                return "unknown: config missing for " + featureFlag;
            }

            var pattern = "\\\"" + Regex.Escape(featureFlag) + "\\\"\\s*:\\s*(true|false)";
            var match = Regex.Match(context.ConfigSource, pattern);
            if (!match.Success)
            {
                return "missing: " + featureFlag;
            }

            return (match.Groups[1].Value == "true" ? "enabled: " : "disabled: ") + featureFlag;
        }

        string PolicyDecisionSummary(CommandDescriptor command)
        {
            return PolicyDecisionSummary(command, ReadPolicyPreviewContext());
        }

        string PolicyDecisionSummary(CommandDescriptor command, PolicyPreviewContext context)
        {
            if (context == null || string.IsNullOrEmpty(context.Status.PolicyPath) || string.IsNullOrEmpty(context.PolicySource))
            {
                return "unknown: policy file missing; Gateway uses defaults by risk";
            }

            if (string.IsNullOrEmpty(context.ProjectPolicySource))
            {
                return "unknown: project policy missing for " + context.Status.ProjectId;
            }

            if (PolicySectionContains(context.ProjectPolicySource, "deny", command.Id))
            {
                return "deny: matched project policy";
            }

            if (PolicySectionContains(context.ProjectPolicySource, "allow", command.Id))
            {
                return "allow: matched project policy";
            }

            if (PolicySectionContains(context.ProjectPolicySource, "ask", command.Id))
            {
                return "ask: matched project policy";
            }

            return "default " + DefaultPolicyAction(context.PolicySource, command.Risk) + ": risk " + command.Risk;
        }

        PolicyPreviewContext ReadPolicyPreviewContext()
        {
            var status = GatewayPolicyManager.GetStatus();
            var configSource = SafeReadAllText(status.ConfigPath);
            var policySource = SafeReadAllText(status.PolicyPath);
            var projectPolicySource = string.IsNullOrEmpty(policySource) ? string.Empty : ProjectPolicySource(policySource, status.ProjectId);
            return new PolicyPreviewContext(status, configSource, policySource, projectPolicySource);
        }

        string ProjectPolicySource(string source, string projectId)
        {
            var projectKey = "\"" + Regex.Escape(projectId) + "\"";
            var match = Regex.Match(source, projectKey + "\\s*:\\s*\\{(?<body>.*?)\\n\\s*\\}", RegexOptions.Singleline);
            return match.Success ? match.Groups["body"].Value : string.Empty;
        }

        PolicyRulesSnapshot ReadProjectPolicyRulesSnapshot(string policyPath, string projectId)
        {
            if (string.IsNullOrEmpty(policyPath) || !File.Exists(policyPath))
            {
                return new PolicyRulesSnapshot("Policy file missing; create config/policy files first.", "None", "None", "None", "unknown");
            }

            var source = File.ReadAllText(policyPath);
            var projectSource = ProjectPolicySource(source, projectId);
            if (string.IsNullOrEmpty(projectSource))
            {
                return new PolicyRulesSnapshot("Project entry missing for '" + projectId + "'.", "None", "None", "None", RiskDefaultsSummary(source));
            }

            var allow = PolicySectionValues(projectSource, "allow");
            var ask = PolicySectionValues(projectSource, "ask");
            var deny = PolicySectionValues(projectSource, "deny");
            return new PolicyRulesSnapshot(
                "allow " + allow.Count + ", ask " + ask.Count + ", deny " + deny.Count + "; feature flags still control command visibility.",
                ValuesSummary(allow),
                ValuesSummary(ask),
                ValuesSummary(deny),
                RiskDefaultsSummary(source));
        }

        ProjectPolicyRulesState ReadProjectPolicyRules(string policyPath, string projectId)
        {
            if (string.IsNullOrEmpty(policyPath) || !File.Exists(policyPath))
            {
                return new ProjectPolicyRulesState("Policy file missing; create config/policy files first.");
            }

            var source = File.ReadAllText(policyPath);
            var projectSource = ProjectPolicySource(source, projectId);
            if (string.IsNullOrEmpty(projectSource))
            {
                return new ProjectPolicyRulesState("Project entry missing for '" + projectId + "'. Save Project Policy will create one with the current draft lists.");
            }

            var allow = PolicySectionValues(projectSource, "allow");
            var ask = PolicySectionValues(projectSource, "ask");
            var deny = PolicySectionValues(projectSource, "deny");
            return new ProjectPolicyRulesState(
                "Loaded saved policy for '" + projectId + "': allow " + allow.Count + ", ask " + ask.Count + ", deny " + deny.Count + ".",
                allow,
                ask,
                deny);
        }

        List<string> PolicySectionValues(string source, string section)
        {
            var values = new List<string>();
            foreach (Match sectionMatch in Regex.Matches(source, "\\\"" + section + "\\\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline))
            {
                foreach (Match valueMatch in Regex.Matches(sectionMatch.Groups[1].Value, "\\\"([^\\\"]+)\\\""))
                {
                    values.Add(valueMatch.Groups[1].Value);
                }
            }

            return values;
        }

        string PolicyEditorText(List<string> values)
        {
            return values == null || values.Count == 0 ? string.Empty : string.Join("\n", values.ToArray());
        }

        List<string> PolicyEditorPatterns(string text, string section, out string error)
        {
            error = string.Empty;
            var values = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var normalized = (text ?? string.Empty).Replace(",", "\n");
            var lines = normalized.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < lines.Length; index++)
            {
                var value = lines[index].Trim();
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                if (value.Length > 160)
                {
                    error = section + " pattern is too long: " + Bounded(value, 80);
                    return values;
                }

                if (value.IndexOfAny(new[] { '"', '\\', '[', ']', '{', '}', ':', ';' }) >= 0)
                {
                    error = section + " pattern contains unsupported JSON/control characters: " + value;
                    return values;
                }

                if (!Regex.IsMatch(value, "^[A-Za-z0-9_.\\-*]+$"))
                {
                    error = section + " pattern contains unsupported characters: " + value;
                    return values;
                }

                if (value.IndexOf('*') >= 0 && value != "*" && !value.EndsWith(".*", StringComparison.Ordinal))
                {
                    error = section + " wildcard must be '*' or a suffix wildcard like asset.*: " + value;
                    return values;
                }

                if (!seen.Add(value))
                {
                    error = section + " pattern is duplicated: " + value;
                    return values;
                }

                values.Add(value);
            }

            if (values.Count > 240)
            {
                error = section + " has too many patterns: " + values.Count + " (max 240).";
            }

            return values;
        }

        string DuplicatePolicyPatternAcrossSections(List<string> allow, List<string> ask, List<string> deny)
        {
            var owners = new Dictionary<string, string>(StringComparer.Ordinal);
            var sections = new[] { "allow", "ask", "deny" };
            var lists = new[] { allow, ask, deny };
            for (var sectionIndex = 0; sectionIndex < sections.Length; sectionIndex++)
            {
                var section = sections[sectionIndex];
                var values = lists[sectionIndex];
                for (var valueIndex = 0; valueIndex < values.Count; valueIndex++)
                {
                    var value = values[valueIndex];
                    if (owners.ContainsKey(value))
                    {
                        return "Pattern '" + value + "' appears in both " + owners[value] + " and " + section + ". Move it to one section before saving.";
                    }

                    owners[value] = section;
                }
            }

            return string.Empty;
        }

        string ProjectPolicyChangeReview(List<string> allow, List<string> ask, List<string> deny)
        {
            var savedAllow = PolicyEditorPatterns(m_policyEditorSavedAllow, "saved allow", out _);
            var savedAsk = PolicyEditorPatterns(m_policyEditorSavedAsk, "saved ask", out _);
            var savedDeny = PolicyEditorPatterns(m_policyEditorSavedDeny, "saved deny", out _);
            return "Project: " + GatewayPolicyManager.GetStatus().ProjectId + "\n"
                + SectionChangeReview("allow", savedAllow, allow) + "\n"
                + SectionChangeReview("ask", savedAsk, ask) + "\n"
                + SectionChangeReview("deny", savedDeny, deny) + "\n"
                + BroadPolicyPatternWarnings(allow, ask, deny) + "\n"
                + "Feature flags and risk defaults are unchanged; approvals are not created, consumed or deleted.";
        }

        string ProjectPolicyExactDraftChangesSummary(List<string> allow, List<string> ask, List<string> deny)
        {
            var savedAllow = PolicyEditorPatterns(m_policyEditorSavedAllow, "saved allow", out _);
            var savedAsk = PolicyEditorPatterns(m_policyEditorSavedAsk, "saved ask", out _);
            var savedDeny = PolicyEditorPatterns(m_policyEditorSavedDeny, "saved deny", out _);
            var rows = new List<string>();
            var exactIds = new SortedSet<string>(StringComparer.Ordinal);
            AddExactPolicyIds(exactIds, savedAllow);
            AddExactPolicyIds(exactIds, savedAsk);
            AddExactPolicyIds(exactIds, savedDeny);
            AddExactPolicyIds(exactIds, allow);
            AddExactPolicyIds(exactIds, ask);
            AddExactPolicyIds(exactIds, deny);
            foreach (var commandId in exactIds)
            {
                var saved = ExactPolicyAction(commandId, savedAllow, savedAsk, savedDeny);
                var draft = ExactPolicyAction(commandId, allow, ask, deny);
                if (!string.Equals(saved, draft, StringComparison.Ordinal))
                {
                    rows.Add(commandId + ": " + saved + " -> " + draft);
                }
            }

            return rows.Count == 0
                ? "Exact command changes: none. The Commands policy action filter 'draft_changed' compares exact command ids only; wildcard effects stay in Review Draft warnings."
                : "Exact command changes (" + rows.Count + "): " + Bounded(string.Join("; ", rows.ToArray()), 900);
        }

        bool CommandPolicyDraftExactChanged(CommandDescriptor descriptor)
        {
            if (descriptor == null || string.IsNullOrEmpty(descriptor.Id))
            {
                return false;
            }

            if (!TryReadProjectPolicyDraft(out var allow, out var ask, out var deny, out _))
            {
                return false;
            }

            var savedAllow = PolicyEditorPatterns(m_policyEditorSavedAllow, "saved allow", out _);
            var savedAsk = PolicyEditorPatterns(m_policyEditorSavedAsk, "saved ask", out _);
            var savedDeny = PolicyEditorPatterns(m_policyEditorSavedDeny, "saved deny", out _);
            var saved = ExactPolicyAction(descriptor.Id, savedAllow, savedAsk, savedDeny);
            var draft = ExactPolicyAction(descriptor.Id, allow, ask, deny);
            return !string.Equals(saved, draft, StringComparison.Ordinal);
        }

        string CommandPolicyDraftExactChangeSummary(CommandDescriptor command)
        {
            if (command == null || string.IsNullOrEmpty(command.Id))
            {
                return "none";
            }

            if (!TryReadProjectPolicyDraft(out var allow, out var ask, out var deny, out var error))
            {
                return "invalid draft: " + error;
            }

            var savedAllow = PolicyEditorPatterns(m_policyEditorSavedAllow, "saved allow", out _);
            var savedAsk = PolicyEditorPatterns(m_policyEditorSavedAsk, "saved ask", out _);
            var savedDeny = PolicyEditorPatterns(m_policyEditorSavedDeny, "saved deny", out _);
            var saved = ExactPolicyAction(command.Id, savedAllow, savedAsk, savedDeny);
            var draft = ExactPolicyAction(command.Id, allow, ask, deny);
            return string.Equals(saved, draft, StringComparison.Ordinal)
                ? "unchanged exact rule: " + saved
                : saved + " -> " + draft + " (exact project rule draft)";
        }

        string CommandPolicyDraftExactAction(CommandDescriptor command)
        {
            if (command == null || string.IsNullOrEmpty(command.Id))
            {
                return "unknown";
            }

            if (!TryReadProjectPolicyDraft(out var allow, out var ask, out var deny, out _))
            {
                return "unknown";
            }

            return ExactPolicyAction(command.Id, allow, ask, deny);
        }

        void AddExactPolicyIds(SortedSet<string> target, List<string> values)
        {
            for (var index = 0; index < values.Count; index++)
            {
                if (IsExactPolicyPattern(values[index]))
                {
                    target.Add(values[index]);
                }
            }
        }

        bool IsExactPolicyPattern(string pattern)
        {
            return !string.IsNullOrEmpty(pattern) && pattern.IndexOf('*') < 0;
        }

        string ExactPolicyAction(string commandId, List<string> allow, List<string> ask, List<string> deny)
        {
            if (ExactPolicyListContains(deny, commandId))
            {
                return "deny";
            }

            if (ExactPolicyListContains(allow, commandId))
            {
                return "allow";
            }

            if (ExactPolicyListContains(ask, commandId))
            {
                return "ask";
            }

            return "none";
        }

        bool ExactPolicyListContains(List<string> values, string commandId)
        {
            for (var index = 0; index < values.Count; index++)
            {
                if (string.Equals(values[index], commandId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        string SectionChangeReview(string section, List<string> saved, List<string> draft)
        {
            var savedSet = new HashSet<string>(saved, StringComparer.Ordinal);
            var draftSet = new HashSet<string>(draft, StringComparer.Ordinal);
            var added = new List<string>();
            var removed = new List<string>();
            for (var index = 0; index < draft.Count; index++)
            {
                if (!savedSet.Contains(draft[index]))
                {
                    added.Add(draft[index]);
                }
            }

            for (var index = 0; index < saved.Count; index++)
            {
                if (!draftSet.Contains(saved[index]))
                {
                    removed.Add(saved[index]);
                }
            }

            return section + ": " + saved.Count + " -> " + draft.Count
                + " (+" + added.Count + "/-" + removed.Count + ")"
                + "; added: " + ValuesSummary(added)
                + "; removed: " + ValuesSummary(removed);
        }

        string BroadPolicyPatternWarnings(List<string> allow, List<string> ask, List<string> deny)
        {
            var warnings = new List<string>();
            AddBroadPolicyPatternWarnings(warnings, "allow", allow, "allow", allow);
            AddBroadPolicyPatternWarnings(warnings, "allow", allow, "ask", ask);
            AddBroadPolicyPatternWarnings(warnings, "allow", allow, "deny", deny);
            AddBroadPolicyPatternWarnings(warnings, "ask", ask, "allow", allow);
            AddBroadPolicyPatternWarnings(warnings, "ask", ask, "ask", ask);
            AddBroadPolicyPatternWarnings(warnings, "ask", ask, "deny", deny);
            AddBroadPolicyPatternWarnings(warnings, "deny", deny, "allow", allow);
            AddBroadPolicyPatternWarnings(warnings, "deny", deny, "ask", ask);
            AddBroadPolicyPatternWarnings(warnings, "deny", deny, "deny", deny);
            return warnings.Count == 0
                ? "Broad pattern warnings: none."
                : "Broad pattern warnings: " + Bounded(string.Join("; ", warnings.ToArray()), 900);
        }

        void AddBroadPolicyPatternWarnings(List<string> warnings, string broadSection, List<string> broadValues, string specificSection, List<string> specificValues)
        {
            for (var broadIndex = 0; broadIndex < broadValues.Count && warnings.Count < 8; broadIndex++)
            {
                var broad = broadValues[broadIndex];
                if (!IsBroadPolicyPattern(broad))
                {
                    continue;
                }

                for (var specificIndex = 0; specificIndex < specificValues.Count && warnings.Count < 8; specificIndex++)
                {
                    var specific = specificValues[specificIndex];
                    if (string.Equals(broad, specific, StringComparison.Ordinal) || IsBroadPolicyPattern(specific))
                    {
                        continue;
                    }

                    if (CommandMatchesPolicyPattern(specific, broad))
                    {
                        warnings.Add(broadSection + " " + broad + " covers " + specificSection + " " + specific + (broadSection == specificSection ? " (specific entry is redundant)" : " (specific entry may be shadowed by local preview order)"));
                    }
                }
            }
        }

        bool IsBroadPolicyPattern(string pattern)
        {
            return pattern == "*" || (!string.IsNullOrEmpty(pattern) && pattern.EndsWith(".*", StringComparison.Ordinal));
        }

        NodeRuntimeInstaller.InstallResult SaveProjectPolicyRules(GatewayPolicyManager.PolicyStatus status, List<string> allow, List<string> ask, List<string> deny)
        {
            var ensureResult = GatewayPolicyManager.EnsureUserConfig();
            if (!ensureResult.Success)
            {
                return ensureResult;
            }

            status = GatewayPolicyManager.GetStatus();
            if (string.IsNullOrEmpty(status.PolicyPath) || !File.Exists(status.PolicyPath))
            {
                return NodeRuntimeInstaller.InstallResult.Fail("Policy file missing after config creation: " + status.PolicyPath);
            }

            try
            {
                var source = File.ReadAllText(status.PolicyPath);
                var updated = UpsertProjectPolicyRules(source, status.ProjectId, allow, ask, deny);
                AtomicWriteText(status.PolicyPath, updated);
                McpFtueProfileTracker.RecordExplicitProfile(ProjectPaths.ProjectRoot, "custom", McpFtueProfileTracker.RulesFingerprint(allow, ask, deny));
                return NodeRuntimeInstaller.InstallResult.Ok("Saved policy for project '" + status.ProjectId + "'. The running Gateway reloads valid policy live; use Refresh Health to verify.");
            }
            catch (Exception exception)
            {
                return NodeRuntimeInstaller.InstallResult.Fail("Could not save project policy: " + exception.Message);
            }
        }

        string UpsertProjectPolicyRules(string source, string projectId, List<string> allow, List<string> ask, List<string> deny)
        {
            var projectBlock = ProjectPolicyObjectBlock(allow, ask, deny);
            var rootStart = source.IndexOf('{');
            var rootEnd = FindJsonObjectEnd(source, rootStart);
            if (rootStart < 0 || rootEnd <= rootStart)
            {
                throw new InvalidOperationException("Policy file root JSON object was not found.");
            }

            if (!TryFindJsonObjectProperty(source, rootStart + 1, rootEnd - 1, "projects", out _, out var projectsStart, out var projectsEnd))
            {
                var rootPrefix = source.Substring(0, rootEnd).TrimEnd();
                var comma = rootPrefix.EndsWith("{", StringComparison.Ordinal) ? string.Empty : ",";
                return rootPrefix + comma + "\n  \"projects\": {\n    "
                    + JsonUtil.String(projectId) + ": " + projectBlock + "\n  }\n"
                    + source.Substring(rootEnd);
            }

            if (TryFindJsonObjectProperty(source, projectsStart + 1, projectsEnd - 1, projectId, out _, out var projectStart, out var projectEnd))
            {
                return source.Substring(0, projectStart) + projectBlock + source.Substring(projectEnd + 1);
            }

            var projectsPrefix = source.Substring(0, projectsEnd).TrimEnd();
            var projectsComma = projectsPrefix.EndsWith("{", StringComparison.Ordinal) ? string.Empty : ",";
            return projectsPrefix + projectsComma + "\n    "
                + JsonUtil.String(projectId) + ": " + projectBlock + "\n"
                + source.Substring(projectsEnd);
        }

        string ProjectPolicyObjectBlock(List<string> allow, List<string> ask, List<string> deny)
        {
            var builder = new StringBuilder();
            builder.Append("{\n");
            AppendPolicyArray(builder, "allow", allow, true);
            AppendPolicyArray(builder, "ask", ask, true);
            AppendPolicyArray(builder, "deny", deny, false);
            builder.Append("    }");
            return builder.ToString();
        }

        void AppendPolicyArray(StringBuilder builder, string name, List<string> values, bool comma)
        {
            builder.Append("      \"").Append(name).Append("\": [\n");
            for (var index = 0; index < values.Count; index++)
            {
                builder.Append("        ").Append(JsonUtil.String(values[index]));
                if (index < values.Count - 1)
                {
                    builder.Append(',');
                }

                builder.Append('\n');
            }

            builder.Append("      ]");
            if (comma)
            {
                builder.Append(',');
            }

            builder.Append('\n');
        }

        bool TryFindJsonObjectProperty(string source, int searchStart, int searchEnd, string propertyName, out int keyStart, out int objectStart, out int objectEnd)
        {
            keyStart = -1;
            objectStart = -1;
            objectEnd = -1;
            if (searchStart < 0 || searchEnd <= searchStart || searchEnd > source.Length)
            {
                return false;
            }

            var pattern = Regex.Escape(JsonUtil.String(propertyName)) + "\\s*:\\s*\\{";
            var searchText = source.Substring(searchStart, searchEnd - searchStart + 1);
            foreach (Match match in Regex.Matches(searchText, pattern, RegexOptions.Singleline))
            {
                keyStart = searchStart + match.Index;
                objectStart = source.IndexOf('{', keyStart + match.Length - 1);
                objectEnd = FindJsonObjectEnd(source, objectStart);
                if (objectStart >= searchStart && objectEnd > objectStart && objectEnd <= searchEnd + 1)
                {
                    return true;
                }
            }

            keyStart = -1;
            objectStart = -1;
            objectEnd = -1;
            return false;
        }

        int FindJsonObjectEnd(string source, int objectStart)
        {
            if (string.IsNullOrEmpty(source) || objectStart < 0 || objectStart >= source.Length || source[objectStart] != '{')
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

        void AtomicWriteText(string path, string content)
        {
            var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            var backupPath = path + ".bak-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            File.WriteAllText(tempPath, content, Encoding.UTF8);
            try
            {
                File.Replace(tempPath, path, backupPath, true);
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
            }
            catch
            {
                File.Copy(tempPath, path, true);
                File.Delete(tempPath);
            }
        }

        bool PolicySectionContains(string source, string section, string commandId)
        {
            foreach (Match sectionMatch in Regex.Matches(source, "\\\"" + section + "\\\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline))
            {
                foreach (Match valueMatch in Regex.Matches(sectionMatch.Groups[1].Value, "\\\"([^\\\"]+)\\\""))
                {
                    if (CommandMatchesPolicyPattern(commandId, valueMatch.Groups[1].Value))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        bool CommandMatchesPolicyPattern(string commandId, string pattern)
        {
            if (pattern == "*")
            {
                return true;
            }

            if (commandId == pattern)
            {
                return true;
            }

            return pattern.EndsWith(".*", StringComparison.Ordinal) && commandId.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.Ordinal);
        }

        string DefaultPolicyAction(string source, string risk)
        {
            var pattern = "\\\"" + Regex.Escape(risk) + "\\\"\\s*:\\s*\\\"(allow|ask|deny)\\\"";
            var match = Regex.Match(source, pattern);
            return match.Success ? match.Groups[1].Value : "unknown";
        }

        string RiskDefaultsSummary(string source)
        {
            return "read=" + DefaultPolicyAction(source, "read")
                + ", write=" + DefaultPolicyAction(source, "write")
                + ", execute=" + DefaultPolicyAction(source, "execute")
                + ", destructive=" + DefaultPolicyAction(source, "destructive")
                + ", network=" + DefaultPolicyAction(source, "network");
        }

        string ValuesSummary(List<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return "None";
            }

            var builder = new StringBuilder();
            var shown = Math.Min(values.Count, 16);
            for (var index = 0; index < shown; index++)
            {
                if (builder.Length > 0)
                {
                    builder.Append(", ");
                }
                builder.Append(values[index]);
            }

            if (values.Count > shown)
            {
                builder.Append(", +");
                builder.Append(values.Count - shown);
                builder.Append(" more");
            }

            return Bounded(builder.ToString(), 900);
        }

        string ReadApprovalsDir(string configPath)
        {
            if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
            {
                return Path.Combine(UnityMcpPackagePaths.LocalAppDataRoot, "approvals") + " (default; config missing)";
            }

            var source = File.ReadAllText(configPath);
            var approvalsIndex = source.IndexOf("\"approvals\"", StringComparison.Ordinal);
            if (approvalsIndex < 0)
            {
                return Path.Combine(UnityMcpPackagePaths.LocalAppDataRoot, "approvals") + " (default; approvals config missing)";
            }

            var dirIndex = source.IndexOf("\"dir\"", approvalsIndex, StringComparison.Ordinal);
            if (dirIndex < 0)
            {
                return Path.Combine(UnityMcpPackagePaths.LocalAppDataRoot, "approvals") + " (default; approvals dir missing)";
            }

            var colonIndex = source.IndexOf(':', dirIndex);
            var configured = colonIndex < 0 ? string.Empty : ReadJsonStringValueAt(source, colonIndex + 1, string.Empty);
            if (string.IsNullOrEmpty(configured))
            {
                return Path.Combine(UnityMcpPackagePaths.LocalAppDataRoot, "approvals") + " (default; approvals dir unreadable)";
            }

            return configured.Replace("%LOCALAPPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        }

        string ReadApprovalsSummary(string approvalsDir, string projectId)
        {
            var dir = approvalsDir.Split(new[] { " (" }, StringSplitOptions.None)[0];
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                return "No local approvals directory found";
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                return "Approval records unreadable: " + ex.Message;
            }

            Array.Sort(files, (left, right) => File.GetLastWriteTimeUtc(right).CompareTo(File.GetLastWriteTimeUtc(left)));
            var now = DateTime.UtcNow;
            var shown = 0;
            var summary = string.Empty;
            foreach (var file in files)
            {
                string source;
                try
                {
                    source = File.ReadAllText(file);
                }
                catch
                {
                    continue;
                }

                var project = ReadJsonStringFieldFromSource(source, "project", string.Empty);
                if (!string.IsNullOrEmpty(projectId) && project != projectId)
                {
                    continue;
                }

                var expiresAtText = ReadJsonStringFieldFromSource(source, "expires_at", string.Empty);
                if (!DateTime.TryParse(expiresAtText, out var expiresAt))
                {
                    continue;
                }

                var remainingMs = Math.Max(0, (long)(expiresAt.ToUniversalTime() - now).TotalMilliseconds);
                var status = remainingMs > 0 ? "active" : "expired";
                var command = ReadJsonStringFieldFromSource(source, "command", "unknown");
                var requestId = ReadJsonStringFieldFromSource(source, "request_id", "none");
                var argsHash = ReadJsonStringFieldFromSource(source, "args_hash", "unknown");
                summary += (shown == 0 ? string.Empty : "\n") + status + " " + project + " " + command + " request_id=" + requestId + " args_hash=" + argsHash + " expires_at=" + expiresAtText + " remaining_ms=" + remainingMs;
                shown++;
                if (shown >= 5)
                {
                    break;
                }
            }

            return shown == 0 ? "No approval records for current project" : summary;
        }

        string ReadPendingApprovalsSummary(string approvalsDir, string projectId)
        {
            var dir = approvalsDir.Split(new[] { " (" }, StringSplitOptions.None)[0];
            var pendingDir = Path.Combine(dir, "pending");
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(pendingDir))
            {
                return "No pending approval requests for current project";
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(pendingDir, "*.json", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                return "Pending approval requests unreadable: " + ex.Message;
            }

            Array.Sort(files, (left, right) => File.GetLastWriteTimeUtc(right).CompareTo(File.GetLastWriteTimeUtc(left)));
            var now = DateTime.UtcNow;
            var shown = 0;
            var summary = string.Empty;
            foreach (var file in files)
            {
                string source;
                try
                {
                    source = File.ReadAllText(file);
                }
                catch
                {
                    continue;
                }

                var project = ReadJsonStringFieldFromSource(source, "project", string.Empty);
                if (!string.IsNullOrEmpty(projectId) && project != projectId)
                {
                    continue;
                }

                var expiresAtText = ReadJsonStringFieldFromSource(source, "expires_at", string.Empty);
                if (!DateTime.TryParse(expiresAtText, out var expiresAt))
                {
                    continue;
                }

                var remainingMs = Math.Max(0, (long)(expiresAt.ToUniversalTime() - now).TotalMilliseconds);
                var status = remainingMs > 0 ? "pending" : "expired";
                var pendingId = ReadJsonStringFieldFromSource(source, "id", "unknown");
                var command = ReadJsonStringFieldFromSource(source, "command", "unknown");
                var risk = ReadJsonStringFieldFromSource(source, "risk", "unknown");
                var requestId = ReadJsonStringFieldFromSource(source, "request_id", "none");
                var argsHash = ReadJsonStringFieldFromSource(source, "args_hash", "unknown");
                var actionHint = ReadJsonStringFieldFromSource(source, "action_hint", "approval approve --pending-id " + pendingId);
                summary += (shown == 0 ? string.Empty : "\n") + status + " " + project + " " + command + " risk=" + risk + " request_id=" + requestId + " pending_id=" + pendingId + " args_hash=" + argsHash + " expires_at=" + expiresAtText + " remaining_ms=" + remainingMs + " action_hint=" + actionHint;
                shown++;
                if (shown >= 5)
                {
                    break;
                }
            }

            return shown == 0 ? "No pending approval requests for current project" : summary;
        }

        string ReadJsonStringFieldFromSource(string source, string fieldName, string fallback)
        {
            var marker = "\"" + fieldName + "\"";
            var fieldIndex = source.IndexOf(marker, StringComparison.Ordinal);
            if (fieldIndex < 0)
            {
                return fallback;
            }

            var colonIndex = source.IndexOf(':', fieldIndex);
            return colonIndex < 0 ? fallback : ReadJsonStringValueAt(source, colonIndex + 1, fallback);
        }

        string GatewayVisibility(CommandDescriptor command, string featureStatus, string policyDecision)
        {
            if (featureStatus.StartsWith("disabled:", StringComparison.Ordinal) || featureStatus.StartsWith("missing:", StringComparison.Ordinal))
            {
                return "hidden: feature_disabled";
            }

            if (policyDecision.StartsWith("deny:", StringComparison.Ordinal) || policyDecision.StartsWith("default deny", StringComparison.Ordinal))
            {
                return "hidden: permission_denied";
            }

            if (command.ResultShape == "handler_unavailable")
            {
                return "visible but unavailable: command_handler_unavailable";
            }

            return "visible when catalog is current and project is online";
        }

        string WhyUnavailable(CommandDescriptor command, string featureStatus, string policyDecision)
        {
            if (featureStatus.StartsWith("disabled:", StringComparison.Ordinal) || featureStatus.StartsWith("missing:", StringComparison.Ordinal))
            {
                return "feature_disabled - enable matching feature flag " + command.FeatureFlag + " in Gateway config, restart Gateway or refresh the catalog, and note that policy alone is not enough";
            }

            if (policyDecision.StartsWith("deny:", StringComparison.Ordinal) || policyDecision.StartsWith("default deny", StringComparison.Ordinal))
            {
                return "permission_denied - current Project Id policy blocks execution; apply the smallest matching profile or edit policy, Refresh Health to verify live reload, and do not bypass policy";
            }

            if (policyDecision.StartsWith("ask:", StringComparison.Ordinal) || policyDecision.StartsWith("default ask", StringComparison.Ordinal))
            {
                return "confirmation_required - visible, but execution needs human approval outside MCP; this UI cannot approve or consume approvals";
            }

            if (command.ResultShape == "handler_unavailable")
            {
                return "command_handler_unavailable - catalog/manifest entry exists, but the handler is unavailable; feature or policy changes alone may not fix it";
            }

            return "none detected from local config/policy preview";
        }

        string ExampleArgsJson(string schemaJson)
        {
            if (string.IsNullOrEmpty(schemaJson) || schemaJson.Contains("\"properties\":{}"))
            {
                return "{}";
            }

            var fields = new List<string>();
            AddExampleField(fields, schemaJson, "command", "\"command\":\"compile.status\"");
            AddExampleField(fields, schemaJson, "query", "\"query\":\"example query\"");
            AddExampleField(fields, schemaJson, "asset_path", "\"asset_path\":\"Assets/Example.asset\"");
            AddExampleField(fields, schemaJson, "scene_path", "\"scene_path\":\"Assets/Scenes/Example.unity\"");
            AddExampleField(fields, schemaJson, "object_path", "\"object_path\":\"ExampleRoot/ExampleChild\"");
            AddExampleField(fields, schemaJson, "package_name", "\"package_name\":\"com.example.package\"");
            AddExampleField(fields, schemaJson, "name", "\"name\":\"ExampleName\"");
            AddExampleField(fields, schemaJson, "content", "\"content\":\"example bounded content\"");
            AddExampleField(fields, schemaJson, "report_id", "\"report_id\":\"example-report\"");
            AddExampleField(fields, schemaJson, "menu_path", "\"menu_path\":\"GameObject/Create Empty\"");
            AddExampleField(fields, schemaJson, "type", "\"type\":\"screenshot\"");
            AddExampleField(fields, schemaJson, "tool", "\"tool\":\"Move\"");
            AddExampleField(fields, schemaJson, "older_than_days", "\"older_than_days\":7");
            AddExampleField(fields, schemaJson, "max_delete", "\"max_delete\":10");
            AddExampleField(fields, schemaJson, "artifact_path", "\"artifact_path\":\"Library/SGGUnityMcp/job-artifacts/run.json\"");
            AddExampleField(fields, schemaJson, "id", "\"id\":\"example-id\"");
            AddExampleField(fields, schemaJson, "paths", "\"paths\":[\"Assets/Scenes/Main.unity\"]");
            AddExampleField(fields, schemaJson, "label", "\"label\":\"Before Edit\"");
            AddExampleField(fields, schemaJson, "mode", "\"mode\":\"editmode\"");
            AddExampleField(fields, schemaJson, "filter", "\"filter\":\"Namespace.Type.TestName\"");
            AddExampleField(fields, schemaJson, "max_results", "\"max_results\":20");
            AddExampleField(fields, schemaJson, "max_chars", "\"max_chars\":20000");
            AddExampleField(fields, schemaJson, "max_seconds", "\"max_seconds\":120");
            AddExampleField(fields, schemaJson, "dry_run", "\"dry_run\":true");
            return fields.Count == 0 ? "{}" : "{" + string.Join(",", fields) + "}";
        }

        string PolicyExplainArgsJson(CommandDescriptor command)
        {
            return command == null ? "{}" : "{\"command\":" + JsonUtil.String(command.Id) + "}";
        }

        string EscapeSnippetString(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        void AddExampleField(List<string> fields, string schemaJson, string fieldName, string example)
        {
            if (SchemaHasProperty(schemaJson, fieldName) && !fields.Contains(example))
            {
                fields.Add(example);
            }
        }

        bool SchemaHasProperty(string schemaJson, string fieldName)
        {
            return !string.IsNullOrEmpty(schemaJson) && schemaJson.Contains("\"" + fieldName + "\":{");
        }

        string EmptyAsNone(string value)
        {
            return string.IsNullOrEmpty(value) ? "none" : value;
        }

        string StripRichText(string value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty : Regex.Replace(value, "<[^>]+>", string.Empty);
        }

        string[] CombineFlags(params string[][] groups)
        {
            var values = new List<string>();
            for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
            {
                var group = groups[groupIndex];
                if (group == null)
                {
                    continue;
                }

                for (var index = 0; index < group.Length; index++)
                {
                    if (!values.Contains(group[index]))
                    {
                        values.Add(group[index]);
                    }
                }
            }

            return values.ToArray();
        }

        sealed class PolicyRulesSnapshot
        {
            public readonly string Summary;
            public readonly string Allow;
            public readonly string Ask;
            public readonly string Deny;
            public readonly string Defaults;

            public PolicyRulesSnapshot(string summary, string allow, string ask, string deny, string defaults)
            {
                Summary = summary;
                Allow = allow;
                Ask = ask;
                Deny = deny;
                Defaults = defaults;
            }
        }

        sealed class PolicyPreviewContext
        {
            public readonly GatewayPolicyManager.PolicyStatus Status;
            public readonly string ConfigSource;
            public readonly string PolicySource;
            public readonly string ProjectPolicySource;

            public PolicyPreviewContext(GatewayPolicyManager.PolicyStatus status, string configSource, string policySource, string projectPolicySource)
            {
                Status = status;
                ConfigSource = configSource ?? string.Empty;
                PolicySource = policySource ?? string.Empty;
                ProjectPolicySource = projectPolicySource ?? string.Empty;
            }
        }

        sealed class ProjectPolicyRulesState
        {
            public readonly string EditorStatus;
            public readonly List<string> Allow;
            public readonly List<string> Ask;
            public readonly List<string> Deny;

            public ProjectPolicyRulesState(string editorStatus)
                : this(editorStatus, new List<string>(), new List<string>(), new List<string>())
            {
            }

            public ProjectPolicyRulesState(string editorStatus, List<string> allow, List<string> ask, List<string> deny)
            {
                EditorStatus = editorStatus;
                Allow = allow;
                Ask = ask;
                Deny = deny;
            }
        }
    }
}
