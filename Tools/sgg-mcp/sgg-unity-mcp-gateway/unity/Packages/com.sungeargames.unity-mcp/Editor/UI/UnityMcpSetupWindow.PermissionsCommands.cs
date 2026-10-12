using System.Collections.Generic;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Commands;
using SGG.UnityMCP.Editor.Setup;
using SGG.UnityMCP.Editor.UI.Localization;
using UnityEngine.UIElements;

namespace SGG.UnityMCP.Editor.UI
{
    public sealed partial class UnityMcpSetupWindow
    {
        Label m_permissionsStatus;
        Label m_permissionsProject;
        Label m_permissionsConfigPath;
        Label m_permissionsPolicyPath;
        Label m_permissionsProjectPolicy;
        Label m_permissionsFeatureHint;
        Label m_permissionsRuntimePolicy;
        Label m_permissionsPolicyRulesSummary;
        Label m_permissionsPolicyRulesAllow;
        Label m_permissionsPolicyRulesAsk;
        Label m_permissionsPolicyRulesDeny;
        Label m_permissionsPolicyRulesDefaults;
        Label m_policyEditorStatus;
        TextField m_policyEditorAllowField;
        TextField m_policyEditorAskField;
        TextField m_policyEditorDenyField;
        PopupField<string> m_policyEditorCommandField;
        PopupField<string> m_policyEditorActionField;
        Label m_policyEditorActionPreview;
        Label m_policyEditorReview;
        Label m_policyEditorExactChanges;
        Label m_permissionsCatalogPolicySummary;
        string m_policyEditorLoadedProjectId = string.Empty;
        string m_policyEditorLoadedPolicyPath = string.Empty;
        string m_policyEditorSavedAllow = string.Empty;
        string m_policyEditorSavedAsk = string.Empty;
        string m_policyEditorSavedDeny = string.Empty;
        bool m_policyEditorRefreshing;
        Label m_permissionsFeatureFlagsSummary;
        Label m_permissionsPreviewRisk;
        Label m_permissionsPreviewFeatureFlag;
        Label m_permissionsPreviewDecision;
        Label m_permissionsPreviewVisibility;
        Label m_permissionsPreviewHumanResult;
        Label m_permissionsPreviewCaveat;
        Label m_permissionsApprovalsDir;
        Label m_permissionsPendingApprovalsList;
        Label m_permissionsApprovalsList;
        Toggle m_permissionsAutoAllowAsk;
        Toggle m_permissionsAutoAllowKnownUnityPopups;
        VisualElement m_permissionsIndicator;
        PopupField<string> m_commandsGroupFilter;
        PopupField<string> m_commandsRiskFilter;
        PopupField<string> m_commandsFeatureFilter;
        PopupField<string> m_commandsPolicyFilter;
        TextField m_commandsSearchField;
        ListView m_commandsList;
        readonly List<CommandDescriptor> m_commandsShown = new List<CommandDescriptor>();
        string m_selectedCommandId = string.Empty;
        Label m_commandsCatalogSummary;
        Label m_commandsStatusBadges;
        Label m_commandsPreview;
        Label m_commandsAvailability;
        Label m_commandsArgsExample;

        void BuildPermissionsPanel()
        {
            HelpRow(m_permissionsPanel, "Configure what the local Gateway exposes to MCP clients. Feature flags make command families visible; policy decides allow/ask/deny for this Project Id; approvals handle ask decisions outside MCP.");

            var guideSection = AddSection(m_permissionsPanel, "How Permissions Work");
            AddRow(guideSection, "Feature flags", "Gateway config switches command families on or off. If a feature flag is disabled, matching commands stay hidden even when policy allows them.");
            AddRow(guideSection, "Policy", "Policy is project-scoped allow/ask/deny rules. Profiles below edit the LocalAppData policy for the current Project Id.");
            AddRow(guideSection, "Profiles", "Readonly is only an optional first smoke test. For real work choose the smallest profile that matches the task, such as Inspect or Safe Writes.");
            AddRow(guideSection, "Approvals", "Ask-gated commands require human approval outside MCP; agents cannot approve themselves.");
            AddRow(guideSection, "Reload behavior", "Valid policy replacements reload live between calls. On a supported runtime, only saved approvals.auto_allow_known_unity_popups also applies on the next request without restart. Other Gateway config, feature flag, approval, token, network or runtime changes require restart.");

            var policySection = AddSection(m_permissionsPanel, "Project Permissions");
            m_permissionsIndicator = AddStatusLine(policySection, out m_permissionsStatus);
            m_permissionsProject = AddRow(policySection, "Project Id");
            m_permissionsConfigPath = AddRow(policySection, "Gateway Config");
            m_permissionsPolicyPath = AddRow(policySection, "Policy File");
            m_permissionsProjectPolicy = AddRow(policySection, "Project Policy");
            m_permissionsRuntimePolicy = AddRow(policySection, "Runtime Policy");
            AddRow(policySection, "Project Policy Help", "Warns if the current Project Id has no policy entry; create config/policy files or apply a profile, then Refresh Health.");
            m_permissionsFeatureHint = AddRow(policySection, "Feature Flags", "Feature flags live in Gateway config; policy/permissions do not enable them. Open Gateway Config to edit flags, then restart Gateway.");
            AddRow(policySection, "Create Config/Policy Files", "Creates user-editable config and policy files if they are missing.");
            AddRow(policySection, "Open Gateway Config", "Advanced manual edit for feature flags and Gateway settings.");
            AddRow(policySection, "Open Policy File", "Advanced manual edit for the current Project Id policy.");
            AddRow(policySection, "Copy Project Policy Snippet", "Copy-only helper; it does not write files or change policy.");
            AddRow(policySection, "Refresh Health", "Confirms that the running Gateway loaded a valid policy replacement. Policy-only changes do not require restart.");
            AddRow(policySection, "Restart Gateway", "Required for other Gateway config, feature flag, approval, token, network or runtime changes. On a supported runtime, saved approvals.auto_allow_known_unity_popups is the only live Gateway config setting.");
            var policyActions = AddActions(policySection);
            AddButton(policyActions, "Create Config/Policy Files", () => RunAction("Create Config/Policy Files", GatewayPolicyManager.EnsureUserConfig));
            AddButton(policyActions, "Open Gateway Config", () => RunAction("Open Gateway Config", GatewayPolicyManager.OpenGatewayConfigFile));
            AddButton(policyActions, "Open Policy File", () => RunAction("Open Policy File", GatewayPolicyManager.OpenPolicyFile));
            AddButton(policyActions, "Copy Project Policy Snippet", () => RunAction("Copy Project Policy Snippet", GatewayPolicyManager.CopyProjectPolicySnippet));
            AddButton(policyActions, "Refresh Health", RefreshHealthAndChecklist);
            var policyRestartGatewayButton = AddButton(policyActions, "Restart Gateway", RestartGateway);
            m_restartGatewayButtons.Add(policyRestartGatewayButton);

            var policyRulesSection = AddSection(m_permissionsPanel, "Project Policy Rules");
            AddRow(policyRulesSection, "Editor scope", "Read-only snapshot of saved policy.json for the current Project Id. This slice does not save drafts, create approvals, consume approvals or execute commands.");
            m_permissionsPolicyRulesSummary = AddRow(policyRulesSection, "Saved project rules");
            m_permissionsPolicyRulesAllow = AddRow(policyRulesSection, "Allow rules");
            m_permissionsPolicyRulesAsk = AddRow(policyRulesSection, "Ask rules");
            m_permissionsPolicyRulesDeny = AddRow(policyRulesSection, "Deny rules");
            m_permissionsPolicyRulesDefaults = AddRow(policyRulesSection, "Risk defaults");
            AddRow(policyRulesSection, "Next step", "Use profile buttons for coarse changes or Open Policy File for manual edits. Save atomically, then Refresh Health to confirm live reload.");
            var policyRulesActions = AddActions(policyRulesSection);
            AddButton(policyRulesActions, "Refresh Rules", RefreshPermissionsAndLocalize);
            AddButton(policyRulesActions, "Copy Policy Rules Summary", CopyPolicyRulesSummary);

            var policyEditorSection = AddSection(m_permissionsPanel, "Project Policy Editor");
            AddRow(policyEditorSection, "Editor scope", "Edits only the current Project Id allow/ask/deny lists in local policy.json. It does not change feature flags, risk defaults, approvals or execute commands.");
            m_policyEditorStatus = AddRow(policyEditorSection, "Draft status");
            AddRow(policyEditorSection, "Pattern format", "One command id or wildcard per line. Use exact ids such as compile.status or suffix wildcards such as asset.*.");
            m_policyEditorAllowField = PolicyPatternsField(policyEditorSection, "Allow patterns");
            m_policyEditorAskField = PolicyPatternsField(policyEditorSection, "Ask patterns");
            m_policyEditorDenyField = PolicyPatternsField(policyEditorSection, "Deny patterns");
            AddRow(policyEditorSection, "Catalog picker", "Select a current catalog command and draft an exact allow/ask/deny rule. Wildcards and bulk edits stay in the raw pattern lists.");
            m_policyEditorCommandField = new PopupField<string>("Catalog command", new List<string> { "catalog unavailable" }, 0);
            m_policyEditorCommandField.AddToClassList("mcp-input-row");
            m_policyEditorCommandField.AddToClassList("mcp-input-field");
            m_policyEditorActionField = new PopupField<string>("Draft action", new List<string> { "allow", "ask", "deny" }, 1);
            m_policyEditorActionField.AddToClassList("mcp-input-row");
            m_policyEditorActionField.AddToClassList("mcp-input-field");
            m_policyEditorActionField.AddToClassList("mcp-policy-action-field");
            m_policyEditorActionField.RegisterValueChangedCallback(_ => UpdatePolicyEditorActionState());
            policyEditorSection.Add(m_policyEditorCommandField);
            policyEditorSection.Add(m_policyEditorActionField);
            m_policyEditorActionPreview = AddRow(policyEditorSection, "Draft action badge");
            UpdatePolicyEditorActionState();
            m_policyEditorReview = AddRow(policyEditorSection, "Change review");
            m_policyEditorExactChanges = AddRow(policyEditorSection, "Exact command changes");
            var policyEditorActions = AddActions(policyEditorSection);
            AddButton(policyEditorActions, "Reload Saved Policy", ReloadProjectPolicyDraft);
            AddButton(policyEditorActions, "Reset Draft", ResetProjectPolicyDraft);
            AddButton(policyEditorActions, "Draft Dropdown Rule", AddPolicyEditorDropdownRule);
            AddButton(policyEditorActions, "Review Draft", ReviewProjectPolicyDraft);
            AddButton(policyEditorActions, "Save Project Policy", SaveProjectPolicyDraft);

            var catalogPolicySection = AddSection(m_permissionsPanel, "Catalog Policy Summary");
            AddRow(catalogPolicySection, "Scope", "Read-only summary of the current Unity Bridge command catalog against feature flags and project policy. It does not execute commands or call policy.explain.");
            m_permissionsCatalogPolicySummary = AddRow(catalogPolicySection, "Current catalog");
            m_permissionsCatalogPolicySummary.enableRichText = true;
            var catalogPolicyActions = AddActions(catalogPolicySection);
            AddButton(catalogPolicyActions, "Refresh Catalog Policy", RefreshPermissionsAndLocalize);
            AddButton(catalogPolicyActions, "Copy Catalog Policy Summary", CopyCatalogPolicySummary);

            var featureSection = AddSection(m_permissionsPanel, "Feature Flags");
            AddRow(featureSection, "Status", "Feature flags are stored in Gateway config. Enable the command families you need, then restart Gateway.");
            m_permissionsFeatureFlagsSummary = AddRow(featureSection, "Current flags");
            AddActionRow(featureSection, "Inspect flags", "Enables semantic scene/prefab/asset/script reads, asset index, local docs, knowledge resources and semantic diffs.", "Enable Inspect Flags", () => RunAction("Enable Inspect Flags", () => GatewayPolicyManager.EnableFeatureFlags("Inspect", GatewayPolicyManager.InspectFeatureFlags)));
            AddActionRow(featureSection, "Safe Writes flags", "Enables bounded asset/script/editor write command families. Policy still decides allow/ask/deny.", "Enable Safe Writes Flags", () => RunAction("Enable Safe Writes Flags", () => GatewayPolicyManager.EnableFeatureFlags("Safe Writes", GatewayPolicyManager.SafeWritesFeatureFlags)));
            AddActionRow(featureSection, "Package flags", "Enables package registry network search/install support. Policy should still gate package mutations.", "Enable Package Flags", () => RunAction("Enable Package Flags", () => GatewayPolicyManager.EnableFeatureFlags("Package Management", GatewayPolicyManager.PackageManagementFeatureFlags)));
            AddActionRow(featureSection, "Test Validation flags", "Enables build/test automation command family visibility. Policy should keep test.run ask-gated.", "Enable Test Flags", () => RunAction("Enable Test Flags", () => GatewayPolicyManager.EnableFeatureFlags("Test Validation", GatewayPolicyManager.TestValidationFeatureFlags)));
            AddActionRow(featureSection, "API/Docs/Profiler flags", "Enables local API reflection, local Unity docs and profiler read-only command families.", "Enable Read Flags", () => RunAction("Enable Read Flags", () => GatewayPolicyManager.EnableFeatureFlags("API/Docs/Profiler", CombineFlags(GatewayPolicyManager.ApiReflectionFeatureFlags, GatewayPolicyManager.UnityDocsFeatureFlags, GatewayPolicyManager.ProfilerReadonlyFeatureFlags))));
            AddActionRow(featureSection, "Profiler Recording flags", "Enables explicit profiler recording and capture export command visibility. Policy should keep these commands ask-gated.", "Enable Profiler Recording Flags", () => RunAction("Enable Profiler Recording Flags", () => GatewayPolicyManager.EnableFeatureFlags("Profiler Recording", GatewayPolicyManager.ProfilerRecordingFeatureFlags)));
            AddActionRow(featureSection, "Runtime flags", "Enables dynamic C# execution and runtime debugging command families; policy still gates execution.", "Enable Runtime Flags", () => RunAction("Enable Runtime Flags", () => GatewayPolicyManager.EnableFeatureFlags("Runtime Debug", GatewayPolicyManager.RuntimeDebugFeatureFlags)));
            AddActionRow(featureSection, "Workflow flags", "Enables diagnostics, plans, skills, checkpoints, artifacts and policy workflow command families.", "Enable Workflow Flags", () => RunAction("Enable Workflow Flags", () => GatewayPolicyManager.EnableFeatureFlags("Workflow", GatewayPolicyManager.WorkflowFeatureFlags)));
            AddActionRow(featureSection, "uGUI / UITK flags", "Enables uGUI inspection and UI Toolkit inspection, validation and safe-write artifact command families.", "Enable UI Flags", () => RunAction("Enable UI Flags", () => GatewayPolicyManager.EnableFeatureFlags("uGUI / UITK", GatewayPolicyManager.UiInspectionFeatureFlags)));
            AddActionRow(featureSection, "All feature flags", "Danger: makes every known command family visible when the Gateway restarts. Policy still gates commands unless Allow All is also applied.", "Enable All Feature Flags", ConfirmEnableAllFeatureFlags, true);

            var profileSection = AddSection(m_permissionsPanel, "Permission Profiles");
            AddProfileRow(profileSection, "Readonly", "Optional first smoke test only. Allows status, logs, package list, loaded scenes and build/test status. Too limited for normal project work. Feature flags: none.", "Apply Readonly", () => ConfirmApplyProfile(GatewayPolicyManager.ReadonlyProfile, "Readonly"), string.Empty, null, false);
            AddProfileRow(profileSection, "Inspect", "Normal starting point for understanding a project without writes. Allows semantic scene/prefab/asset/script reads. Needs related read-only feature flags.", "Apply Inspect", () => ConfirmApplyProfile(GatewayPolicyManager.InspectProfile, "Inspect"), "Apply + Enable Flags", () => ConfirmApplyProfile(GatewayPolicyManager.InspectProfile, "Inspect", true), false);
            AddProfileRow(profileSection, "Safe Writes", "For trusted local projects when the agent should edit assets, scenes, scripts or objects. Writes remain ask-gated. Needs write feature flags.", "Apply Safe Writes", () => ConfirmApplyProfile(GatewayPolicyManager.SafeWritesProfile, "Safe Writes"), "Apply + Enable Flags", () => ConfirmApplyProfile(GatewayPolicyManager.SafeWritesProfile, "Safe Writes", true), false);
            AddProfileRow(profileSection, "Package Mgmt", "For reviewing or changing packages. Allows package status and ask-gates install/remove/search operations. Network search/install also needs package_registry_network.", "Apply Package Mgmt", () => ConfirmApplyProfile(GatewayPolicyManager.PackageManagementProfile, "Package Management"), "Apply + Enable Flags", () => ConfirmApplyProfile(GatewayPolicyManager.PackageManagementProfile, "Package Management", true), false);
            AddProfileRow(profileSection, "Test Validation", "For bounded Unity test validation. Allows status, build/test discovery, logs and artifacts; test.run remains ask-gated and build.run is not included. Needs build_test_automation.", "Apply Test Validation", () => ConfirmApplyProfile(GatewayPolicyManager.TestValidationProfile, "Test Validation"), "Apply + Enable Flags", () => ConfirmApplyProfile(GatewayPolicyManager.TestValidationProfile, "Test Validation", true), false);
            AddProfileRow(profileSection, "API Reflection", "For verifying loaded Unity/editor APIs before code changes. Allows local assembly reflection reads. Needs unity_api_reflection.", "Apply API Reflection", () => ConfirmApplyProfile(GatewayPolicyManager.ApiReflectionProfile, "API Reflection"), "Apply + Enable Flags", () => ConfirmApplyProfile(GatewayPolicyManager.ApiReflectionProfile, "API Reflection", true), false);
            AddProfileRow(profileSection, "Unity Docs", "For querying project-provided local Unity docs. Allows docs query/reference reads. Needs unity_docs_local.", "Apply Unity Docs", () => ConfirmApplyProfile(GatewayPolicyManager.UnityDocsProfile, "Unity Docs"), "Apply + Enable Flags", () => ConfirmApplyProfile(GatewayPolicyManager.UnityDocsProfile, "Unity Docs", true), false);
            AddProfileRow(profileSection, "Profiler Readonly", "For safe profiler availability and already-available frame summaries. Does not start recordings or create captures. Needs profiler_readonly.", "Apply Profiler Readonly", () => ConfirmApplyProfile(GatewayPolicyManager.ProfilerReadonlyProfile, "Profiler Readonly"), "Apply + Enable Flags", () => ConfirmApplyProfile(GatewayPolicyManager.ProfilerReadonlyProfile, "Profiler Readonly", true), false);
            AddProfileRow(profileSection, "Profiler Recording", "For explicit local profiler recording and capture export. Commands stay ask-gated and require confirm arguments. Needs profiler_recording.", "Apply Profiler Recording", () => ConfirmApplyProfile(GatewayPolicyManager.ProfilerRecordingProfile, "Profiler Recording"), "Apply + Enable Flags", () => ConfirmApplyProfile(GatewayPolicyManager.ProfilerRecordingProfile, "Profiler Recording", true), false);
            AddProfileRow(profileSection, "Runtime Debug", "For Play Mode/runtime bugs. Allows bounded snapshots and ask-gates Play Mode or temporal sampling. Needs runtime feature flags.", "Apply Runtime Debug", () => ConfirmApplyProfile(GatewayPolicyManager.RuntimeDebugProfile, "Runtime Debug"), "Apply + Enable Flags", () => ConfirmApplyProfile(GatewayPolicyManager.RuntimeDebugProfile, "Runtime Debug", true), false);
            AddProfileRow(profileSection, "Danger: Allow All", "Danger: allows every built-in/trusted Gateway-visible command for this Project Id without ask gates. Untrusted extension packages remain trust-gated.", "Apply Allow All Policy", () => ConfirmAllowAll(false), "Enable All + Allow All", () => ConfirmAllowAll(true), true);

            var previewSection = AddSection(m_permissionsPanel, "Selected Command Preview");
            AddRow(previewSection, "Status", "Read-only local preview for the command selected on the Commands tab.");
            m_permissionsPreviewRisk = AddRow(previewSection, "Risk");
            m_permissionsPreviewFeatureFlag = AddRow(previewSection, "Feature flag");
            m_permissionsPreviewDecision = AddRow(previewSection, "Policy decision");
            m_permissionsPreviewVisibility = AddRow(previewSection, "Visibility");
            m_permissionsPreviewHumanResult = AddRow(previewSection, "Human result");
            m_permissionsPreviewCaveat = AddRow(previewSection, "Local preview caveat");
            AddRow(previewSection, "policy.explain", "Gateway policy.explain is authoritative and copyable from Commands. This UI never executes policy.explain or the selected command.");
            var previewActions = AddActions(previewSection);
            AddButton(previewActions, "Copy Effective Policy Summary", CopySelectedCommandEffectivePolicySummary);
            AddButton(previewActions, "Draft Allow Rule", () => AddSelectedCommandToPolicyDraft("allow"));
            AddButton(previewActions, "Draft Ask Rule", () => AddSelectedCommandToPolicyDraft("ask"));
            AddButton(previewActions, "Draft Deny Rule", () => AddSelectedCommandToPolicyDraft("deny"));

            var approvalsSection = AddSection(m_permissionsPanel, "One-shot Approvals");
            AddRow(approvalsSection, "Status", "Read-only local visibility. Pending requests are created by confirmation_required; this UI never creates, consumes or deletes approvals and approvals live outside MCP.");
            AddRow(approvalsSection, "Temporary ask bypass", "For clients without native prompts only. Auto-allow ask does not bypass deny, feature flags, schema validation or PathGuard. Disable when OpenCode/OpenChamber native prompts exist.");
            m_permissionsAutoAllowAsk = new Toggle("Auto-allow ask (temporary)");
            m_permissionsAutoAllowAsk.tooltip = "Edits approvals.auto_allow_ask in the user gateway config. Restart Gateway after changing.";
            m_permissionsAutoAllowAsk.RegisterValueChangedCallback(change => SetAutoAllowAsk(change.newValue));
            approvalsSection.Add(m_permissionsAutoAllowAsk);
            var popupRussian = McpWindowLocalization.CurrentLanguage == "ru";
            HelpRow(approvalsSection, popupRussian
                ? "По умолчанию выключено. Явное разрешение агенту выбирать поддерживаемые ответы на известные окна Unity без повторного подтверждения, включая отмену несохранённых изменений, перезагрузку и понижение версии. DENY, флаги функций, точные проверки владельца/окна/процесса и одноразовое выполнение без автоматического повтора сохраняются. auto_allow_ask не изменяется. В Gateway runtime с поддержкой live-reload этой настройки сохранённое включение/отключение действует со следующего запроса без перезапуска; перед отправкой ответа настройка проверяется снова. Действующее состояние смотрите в diagnostics.status popup_response_approval.enabled; галочка показывает сохранённое значение."
                : "Default OFF. Explicit opt-in to agent-chosen supported replies to known Unity popups without repeat confirmation, including discard of unsaved changes, reload and downgrade. DENY, feature flags, exact owner/window/process checks and one-shot execution with no automatic replay remain. auto_allow_ask is unchanged. On a Gateway runtime supporting live popup settings, saved enable/disable changes apply on the next request without restart and are rechecked before response issuance. Check diagnostics.status popup_response_approval.enabled; this checkbox shows the saved setting.");
            m_permissionsAutoAllowKnownUnityPopups = new Toggle(popupRussian
                ? "Не спрашивать снова для известных окон Unity"
                : "Do not ask again for known Unity popups");
            m_permissionsAutoAllowKnownUnityPopups.tooltip = popupRussian
                ? "approvals.auto_allow_known_unity_popups — сохранённое значение; действующее состояние смотрите в diagnostics.status popup_response_approval.enabled."
                : "approvals.auto_allow_known_unity_popups — saved setting; check diagnostics.status popup_response_approval.enabled for the running state.";
            m_permissionsAutoAllowKnownUnityPopups.RegisterValueChangedCallback(change => SetAutoAllowKnownUnityPopups(change.newValue));
            approvalsSection.Add(m_permissionsAutoAllowKnownUnityPopups);
            m_permissionsApprovalsDir = AddRow(approvalsSection, "Approvals dir");
            m_permissionsPendingApprovalsList = AddRow(approvalsSection, "Pending requests");
            m_permissionsApprovalsList = AddRow(approvalsSection, "Approvals list");
            var approvalActions = AddActions(approvalsSection);
            AddButton(approvalActions, "Refresh Approvals", RefreshPermissionsAndLocalize);
        }

        void BuildCommandsPanel()
        {
            HelpRow(m_commandsPanel, "Commands is a read-only catalog browser. Filters, selection and copy buttons are safe; this tab does not run sgg_unity_call or mutate Unity project state.");

            var section = AddSection(m_commandsPanel, "Commands");
            AddRow(section, "Status", "Read-only command catalog display. It reads UnityBridgeRuntime.CommandDescriptors and does not execute Unity commands directly.");
            AddRow(section, "Execution", "Disabled in this UI. This tab does not execute Unity commands directly.");
            m_commandsGroupFilter = new PopupField<string>("Group", new List<string> { "all", "scene", "prefab", "asset", "gameobject", "component", "material", "editor", "package", "build", "test", "runtime", "diff", "knowledge", "unity", "profiler", "diagnostics", "plan", "checkpoint", "artifact", "skill", "policy" }, 0);
            m_commandsRiskFilter = new PopupField<string>("Risk", new List<string> { "all", "read", "write", "execute", "destructive", "network" }, 0);
            m_commandsFeatureFilter = new PopupField<string>("Feature flag", new List<string> { "all", "none", "flagged" }, 0);
            m_commandsPolicyFilter = new PopupField<string>("Policy action", new List<string> { "all", "allow", "ask", "deny", "unknown", "feature_hidden", "draft_changed" }, 0);
            m_commandsSearchField = new TextField("Search command id/title/description");
            m_commandsSearchField.AddToClassList("mcp-input-row");
            m_commandsSearchField.AddToClassList("mcp-input-field");
            m_commandsGroupFilter.RegisterValueChangedCallback(_ => RefreshCommandsAndLocalize());
            m_commandsRiskFilter.RegisterValueChangedCallback(_ => RefreshCommandsAndLocalize());
            m_commandsFeatureFilter.RegisterValueChangedCallback(_ => RefreshCommandsAndLocalize());
            m_commandsPolicyFilter.RegisterValueChangedCallback(_ => RefreshCommandsAndLocalize());
            m_commandsSearchField.RegisterValueChangedCallback(_ => RefreshCommandsAndLocalize());
            section.Add(m_commandsGroupFilter);
            section.Add(m_commandsRiskFilter);
            section.Add(m_commandsFeatureFilter);
            section.Add(m_commandsPolicyFilter);
            section.Add(m_commandsSearchField);
            m_commandsCatalogSummary = AddRow(section, "Catalog");
            m_commandsList = new ListView
            {
                selectionType = SelectionType.Single,
                makeItem = () =>
                {
                    var label = new Label();
                    label.AddToClassList("mcp-command-row");
                    return label;
                },
                bindItem = (element, index) => BindCommandListItem((Label)element, m_commandsShown[index]),
            };
            m_commandsList.AddToClassList("mcp-command-list");
            m_commandsList.selectionChanged += OnCommandSelectionChanged;
            section.Add(m_commandsList);
            m_commandsStatusBadges = AddRow(section, "Status badges");
            m_commandsPreview = AddRow(section, "Metadata preview");
            m_commandsAvailability = AddRow(section, "Availability / why unavailable");
            m_commandsArgsExample = AddRow(section, "args_json template");
            var actions = AddActions(section);
            AddButton(actions, "Copy Command Id", CopySelectedCommandId);
            AddButton(actions, "Copy Schema", CopySelectedCommandSchema);
            AddButton(actions, "Copy args_json Template", CopySelectedCommandArgsExample);
            AddButton(actions, "Copy policy.explain args_json", CopySelectedCommandPolicyExplainArgsJson);
            AddButton(actions, "Copy Effective Policy Summary", CopySelectedCommandEffectivePolicySummary);
            AddButton(actions, "Copy sgg_unity_call Snippet", CopySelectedCommandCallSnippet);
        }
    }
}
