using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Commands;
using SGG.UnityMCP.Editor.Paths;
using SGG.UnityMCP.Editor.Setup;
using SGG.UnityMCP.Editor.UI.Localization;
using SGG.UnityMCP.Editor.UI.State;
using Unity.Profiling;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace SGG.UnityMCP.Editor.UI
{
    public sealed partial class UnityMcpSetupWindow : EditorWindow
    {
        VisualElement m_ftuePanel;
        VisualElement m_setupPanel;
        VisualElement m_statusPanel;
        VisualElement m_permissionsPanel;
        VisualElement m_commandsPanel;
        VisualElement m_plansPanel;
        VisualElement m_skillsPanel;
        VisualElement m_unityAiAssistantPanel;
        VisualElement m_diagnosticsPanel;
        [SerializeField]
        McpWindowState m_windowState = new McpWindowState();
        [SerializeField, HideInInspector]
        string m_currentTab = string.Empty;
        ToolbarToggle m_ftueTab;
        ToolbarToggle m_setupTab;
        ToolbarToggle m_statusTab;
        ToolbarToggle m_permissionsTab;
        ToolbarToggle m_commandsTab;
        ToolbarToggle m_plansTab;
        ToolbarToggle m_skillsTab;
        ToolbarToggle m_unityAiAssistantTab;
        ToolbarToggle m_diagnosticsTab;
        Label m_lastActionLabel;
        VisualElement m_headerShortcutBar;
        Button m_installPortableNodeShortcutButton;
        Button m_installRuntimeShortcutButton;
        Button m_updateRuntimeShortcutButton;
        Button m_addCanonicalPathRegistryShortcutButton;
        Button m_installCanonicalPathShortcutButton;
        Button m_startGatewayShortcutButton;
        Button m_refreshHealthShortcutButton;
        Button m_addPermissionsShortcutButton;
        Button m_allowAllShortcutButton;

        const string UnityAiAssistantPackageId = "com.unity.ai.assistant";
        const string UnityMcpPackageId = "com.sungeargames.unity-mcp";
        const string CanonicalPathPackageId = "com.romanilyin.canonicalpath";
        const string CanonicalPathVersion = "2026.10.10-4";
        const string CanonicalPathRegistryName = "npmjs";
        const string CanonicalPathRegistryUrl = "https://registry.npmjs.org";
        const string CanonicalPathScope = "com.romanilyin";
        const string McpTokenEnvName = "SGG_UNITY_MCP_TOKEN";
        const string McpUrl = "http://127.0.0.1:27182/mcp";
        const string HealthUrl = "http://127.0.0.1:27182/health";
        const string McpConnectionModePrefsKey = "SGG.UnityMCP.McpWindow.ConnectionMode";
        const double HeartbeatWindowRefreshDelaySeconds = 1.0;
        const double HeartbeatStaleWarningSeconds = 90.0;
        const float MinimumWindowWidth = 600f;
        const float MinimumWindowHeight = 260f;
        const float DefaultWindowWidth = 1200f;
        const float DefaultWindowHeight = 720f;
        const string WindowFontName = "Segoe UI";
        const int WindowFontSize = 13;
        const string WindowFallbackFontAssetPath = "Editor/UI/Fonts/NotoSans-Regular.ttf";

        static bool s_windowFontInitialized;
        static Font s_windowFont;
        internal static bool SkipAutomaticRefreshForTests { get; set; } = false;
        // Capture test suppression on the fixture, never on another already-open window.
        bool m_skipAutomaticRefreshForTests;
        bool m_isIsolatedTestFixture;
        bool m_initializingGui;
        Label m_refreshFailureLabel;
        internal bool SkipToolFreshnessForTests { get; set; }
        internal bool IsIsolatedTestFixture => m_isIsolatedTestFixture;
        internal void EnableLocalRefreshForIsolatedTests()
        {
            if (!m_isIsolatedTestFixture) throw new InvalidOperationException("Not an isolated test fixture.");
            m_skipAutomaticRefreshForTests = false;
        }
        readonly List<string> m_refreshFailures = new List<string>();
        static readonly ProfilerMarker s_mcpWindowRefreshMarker = new ProfilerMarker("SGG.MCPWindow.Refresh");
        static readonly ProfilerMarker s_mcpWindowHealthCheckMarker = new ProfilerMarker("SGG.MCPWindow.HealthCheck");
        static readonly ProfilerMarker s_mcpWindowCatalogRefreshMarker = new ProfilerMarker("SGG.MCPWindow.CatalogRefresh");
        static readonly ProfilerMarker s_mcpWindowHeartbeatMarker = new ProfilerMarker("SGG.MCPWindow.HeartbeatReadOrWrite");

        double m_nextHeartbeatWindowRefreshTime = -1.0;

        GatewayProcessManager.GatewayRuntimeStatus m_lastProcessStatus;
        GatewayProcessManager.GatewayRuntimeStatus m_lastProbedProcessStatus;
        readonly List<Button> m_restartGatewayButtons = new List<Button>();
        ScrollView m_mainScrollView;
        string m_lastMcpWindowTimingSummary = "No MCP Window timing captured yet.";
        string m_lastAppliedLocalizationLanguage = string.Empty;
        bool m_localizationDirty = true;
        int m_gatewayProcessActionGeneration;
        static bool s_gatewayProcessActionInProgress;

        internal sealed class McpWindowScrollBenchmarkResult
        {
            public bool Available;
            public string Reason = string.Empty;
            public int StepsRequested;
            public int StepsCompleted;
            public float DeltaPixels;
            public bool Restored;
            public bool RefreshDiagnosticsEachStep;
            public bool RepaintRequested;
            public long TotalMs;
            public long MinStepMs;
            public long MaxStepMs;
            public double AverageStepMs;
            public float OriginalScrollY;
            public float FinalScrollY;
            public string TimingSummary = string.Empty;
        }

        McpWindowState WindowState
        {
            get
            {
                if (m_windowState == null)
                {
                    m_windowState = new McpWindowState();
                }

                if (!string.IsNullOrEmpty(m_currentTab))
                {
                    if (!m_windowState.HasExplicitSelection)
                    {
                        m_windowState.SelectTab(m_currentTab);
                    }

                    m_currentTab = string.Empty;
                }

                return m_windowState;
            }
        }

        [MenuItem("SGG/Unity MCP/MCP Window")]
        public static void Open()
        {
            OpenTab("Setup");
        }

        static void OpenTab(string tabName)
        {
            OpenForCommand(tabName);
        }

        internal static UnityMcpSetupWindow OpenForCommand(string tabName)
        {
            var window = GetWindowWithRect<UnityMcpSetupWindow>(new Rect(100, 100, DefaultWindowWidth, DefaultWindowHeight), false, BuildWindowTitle(InstalledPackageVersion()));
            window.RefreshWindowTitle();
            window.minSize = new Vector2(MinimumWindowWidth, MinimumWindowHeight);
            window.WindowState.SelectTab(tabName);
            window.Show();
            window.SelectTab(window.WindowState.SelectedTab);
            return window;
        }

        internal static bool TryGetOpenWindow(out UnityMcpSetupWindow window, out int openWindowCount)
        {
            var windows = Resources.FindObjectsOfTypeAll<UnityMcpSetupWindow>();
            openWindowCount = windows == null ? 0 : windows.Length;
            window = null;
            if (windows == null)
            {
                return false;
            }

            for (var index = 0; index < windows.Length; index++)
            {
                if (windows[index] != null)
                {
                    window = windows[index];
                    return true;
                }
            }

            return false;
        }

        public void CreateGUI()
        {
            m_initializingGui = true;
            try
            {
                BuildGui();
            }
            finally
            {
                m_initializingGui = false;
            }

            // Optional version checks must not prevent the required local rows from populating.
            if (string.Equals(WindowState.SelectedTab, "Setup", StringComparison.Ordinal))
            {
                TrackMcpWindowStep(new StringBuilder(), "automatic_tool_freshness", TryStartAutomaticUnityToolFreshnessCheck);
            }
            if (!m_skipAutomaticRefreshForTests && !SkipToolFreshnessForTests)
            {
                _ = RefreshTestHistoryAsync();
            }
        }

        void BuildGui()
        {
            RefreshWindowTitle();
            m_localizationDirty = true;
            rootVisualElement.Clear();
            rootVisualElement.AddToClassList("mcp-window");
            ApplyWindowFont(rootVisualElement);
            var stylePath = UnityMcpPackagePaths.PackageAssetPath + "/Editor/UI/UnityMcpSetupWindow.uss";
            var style = AssetDatabase.LoadAssetAtPath<StyleSheet>(stylePath);
            if (style != null)
            {
                rootVisualElement.styleSheets.Add(style);
            }

            var title = new Label("SGG Unity MCP Setup");
            title.AddToClassList("mcp-title");
            rootVisualElement.Add(title);

            m_refreshFailureLabel = new Label { name = "mcp-refresh-failures" };
            m_refreshFailureLabel.AddToClassList("mcp-log");
            m_refreshFailureLabel.style.display = DisplayStyle.None;
            rootVisualElement.Add(m_refreshFailureLabel);

            BuildHeaderShortcuts();
            BuildTabs();
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            m_mainScrollView = scroll;
            rootVisualElement.Add(scroll);

            m_ftuePanel = new VisualElement();
            m_ftuePanel.name = "ftue-panel";
            m_setupPanel = new VisualElement();
            m_setupPanel.name = "setup-panel";
            m_statusPanel = new VisualElement();
            m_permissionsPanel = new VisualElement();
            m_commandsPanel = new VisualElement();
            m_plansPanel = new VisualElement();
            m_skillsPanel = new VisualElement();
            m_unityAiAssistantPanel = new VisualElement();
            m_diagnosticsPanel = new VisualElement();
            scroll.Add(m_ftuePanel);
            scroll.Add(m_setupPanel);
            scroll.Add(m_statusPanel);
            scroll.Add(m_permissionsPanel);
            scroll.Add(m_commandsPanel);
            scroll.Add(m_plansPanel);
            scroll.Add(m_skillsPanel);
            scroll.Add(m_unityAiAssistantPanel);
            scroll.Add(m_diagnosticsPanel);

            BuildFtuePanel();
            BuildSetupPanel();
            BuildStatusPanel();
            BuildPermissionsPanel();
            BuildCommandsPanel();
            BuildPlansPanel();
            BuildSkillsPanel();
            BuildUnityAiAssistantPanel();
            BuildDiagnosticsPanel();

            m_lastActionLabel = new Label();
            m_lastActionLabel.AddToClassList("mcp-log");
            rootVisualElement.Add(m_lastActionLabel);

            if (!m_skipAutomaticRefreshForTests)
            {
                RefreshAll(true);
            }
            SelectTab(WindowState.SelectedTab);
        }

        static void ApplyWindowFont(VisualElement root)
        {
            var font = GetWindowFont();
            if (font != null)
            {
                root.style.unityFont = font;
            }
        }

        static Font GetWindowFont()
        {
            if (s_windowFontInitialized)
            {
                return s_windowFont;
            }

            s_windowFontInitialized = true;
            s_windowFont = CreateOsWindowFont();
            if (s_windowFont == null)
            {
                s_windowFont = LoadBundledWindowFont();
            }

            return s_windowFont;
        }

        static Font CreateOsWindowFont()
        {
            try
            {
                var installedFonts = Font.GetOSInstalledFontNames();
                for (var index = 0; index < installedFonts.Length; index++)
                {
                    if (string.Equals(installedFonts[index], WindowFontName, StringComparison.OrdinalIgnoreCase))
                    {
                        return Font.CreateDynamicFontFromOSFont(WindowFontName, WindowFontSize);
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("SGG Unity MCP setup window could not use " + WindowFontName + ": " + exception.Message);
            }

            return null;
        }

        static Font LoadBundledWindowFont()
        {
            var fontPath = UnityMcpPackagePaths.PackageAssetPath + "/" + WindowFallbackFontAssetPath;
            var font = AssetDatabase.LoadAssetAtPath<Font>(fontPath);
            if (font == null)
            {
                Debug.LogWarning("SGG Unity MCP setup window could not load bundled fallback font at " + fontPath + ".");
            }

            return font;
        }

        void OnEnable()
        {
            m_skipAutomaticRefreshForTests = SkipAutomaticRefreshForTests;
            m_isIsolatedTestFixture = SkipAutomaticRefreshForTests;
            RefreshWindowTitle();
            UnityBridgeRuntime.HeartbeatWritten -= ScheduleWindowRefreshAfterHeartbeat;
            UnityBridgeRuntime.HeartbeatWritten += ScheduleWindowRefreshAfterHeartbeat;
            if (!m_isIsolatedTestFixture) ResumeUnityCliOperations();
        }

        void OnDisable()
        {
            m_gatewayProcessActionGeneration++;
            UnityBridgeRuntime.HeartbeatWritten -= ScheduleWindowRefreshAfterHeartbeat;
            EditorApplication.update -= RefreshWindowAfterHeartbeat;
            EditorApplication.update -= PollCanonicalPathInstall;
            EditorApplication.update -= PollUnityAiAssistantInstall;
            HistoryBusyChanged -= UpdateHistoryButtons;
            // Synthetic windows do not own the owner's shared static tool operations.
            if (m_isIsolatedTestFixture) return;
            EditorApplication.update -= PollWindowsUnityCliInstall;
            CancelWindowsUnityCliInstall();
            EditorApplication.update -= PollUnityPipelineInstall;
            EditorApplication.update -= PollUnityPipelineVerification;
            EditorApplication.update -= PollUnityCliProjectSkillInstall;
            EditorApplication.update -= PollWslUnityCliWrapperInstall;
            EditorApplication.update -= PollUnityToolUpdateCheck;
            CancelUnityToolUpdateCheck();
            EditorApplication.update -= PollUnityToolLocalStateRefresh;
            CancelUnityToolLocalStateRefresh();
            EditorApplication.update -= PollWindowsUnityCliUpdate;
            CancelWindowsUnityCliUpdate();
            EditorApplication.update -= PollUnityPipelineUpdate;
            CancelUnityPipelineUpdate();
        }

        internal static string BuildWindowTitle(string packageVersion)
        {
            return "SGG Unity MCP - " + (string.IsNullOrWhiteSpace(packageVersion) ? "package version unavailable" : packageVersion.Trim());
        }

        static string InstalledPackageVersion()
        {
            try
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UnityMcpSetupWindow).Assembly);
                return package != null && string.Equals(package.name, UnityMcpPackageId, StringComparison.Ordinal) ? package.version : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        void RefreshWindowTitle()
        {
            titleContent = new GUIContent(BuildWindowTitle(InstalledPackageVersion()));
        }

        void BuildTabs()
        {
            var toolbar = new Toolbar();
            toolbar.AddToClassList("mcp-tabs");
            m_ftueTab = new ToolbarToggle { text = "FTUE" };
            m_setupTab = new ToolbarToggle { text = "Setup" };
            m_statusTab = new ToolbarToggle { text = "Status" };
            m_permissionsTab = new ToolbarToggle { text = "Permissions" };
            m_commandsTab = new ToolbarToggle { text = "Commands" };
            m_plansTab = new ToolbarToggle { text = "Plans" };
            m_skillsTab = new ToolbarToggle { text = "Skills" };
            m_unityAiAssistantTab = new ToolbarToggle { text = "Unity AI" };
            m_diagnosticsTab = new ToolbarToggle { text = "Diagnostics" };
            m_ftueTab.AddToClassList("mcp-tab");
            m_setupTab.AddToClassList("mcp-tab");
            m_statusTab.AddToClassList("mcp-tab");
            m_permissionsTab.AddToClassList("mcp-tab");
            m_commandsTab.AddToClassList("mcp-tab");
            m_plansTab.AddToClassList("mcp-tab");
            m_skillsTab.AddToClassList("mcp-tab");
            m_unityAiAssistantTab.AddToClassList("mcp-tab");
            m_diagnosticsTab.AddToClassList("mcp-tab");
            RegisterTab(m_ftueTab, "FTUE");
            RegisterTab(m_setupTab, "Setup");
            RegisterTab(m_statusTab, "Status");
            RegisterTab(m_permissionsTab, "Permissions");
            RegisterTab(m_commandsTab, "Commands");
            RegisterTab(m_plansTab, "Plans");
            RegisterTab(m_skillsTab, "Skills");
            RegisterTab(m_unityAiAssistantTab, "Unity AI Assistant");
            RegisterTab(m_diagnosticsTab, "Diagnostics");
            toolbar.Add(m_ftueTab);
            toolbar.Add(m_setupTab);
            toolbar.Add(m_statusTab);
            toolbar.Add(m_permissionsTab);
            toolbar.Add(m_commandsTab);
            toolbar.Add(m_plansTab);
            toolbar.Add(m_skillsTab);
            toolbar.Add(m_unityAiAssistantTab);
            toolbar.Add(m_diagnosticsTab);
            rootVisualElement.Add(toolbar);
        }

        void BuildHeaderShortcuts()
        {
            m_headerShortcutBar = new VisualElement();
            m_headerShortcutBar.AddToClassList("mcp-header-shortcuts");
            m_installPortableNodeShortcutButton = AddHeaderShortcutButton("N+", "Install Portable Node", () => RunAction("Install Portable Node", () => NodeRuntimeInstaller.InstallPortableNode(true)));
            m_installRuntimeShortcutButton = AddHeaderShortcutButton("R+", "Install Gateway runtime", () => RunAction("Install Gateway Runtime", InstallGatewayRuntime, true));
            m_updateRuntimeShortcutButton = AddHeaderShortcutButton("R↑", "Update Gateway runtime", () => RunAction("Update Gateway Runtime", InstallGatewayRuntime, true));
            m_addCanonicalPathRegistryShortcutButton = AddHeaderShortcutButton("S+", "Add CanonicalPath scoped registry", () => RunAction("Add Scoped Registry", ConfigureCanonicalPathRegistry));
            m_installCanonicalPathShortcutButton = AddHeaderShortcutButton("CP+", "Install CanonicalPath", InstallCanonicalPathPackage);
            m_startGatewayShortcutButton = AddHeaderShortcutButton("G*", "Start or Restart Gateway", RestartGateway);
            m_refreshHealthShortcutButton = AddHeaderShortcutButton("H*", "Refresh Gateway health", () => RefreshAll(true));
            m_addPermissionsShortcutButton = AddHeaderShortcutButton("P+", "Add baseline project permissions", AddBaselinePermissions);
            m_allowAllShortcutButton = AddHeaderShortcutButton("P∞", "Enable all feature flags and apply Allow All policy", () => ConfirmAllowAll(true));
            m_allowAllShortcutButton.AddToClassList("mcp-danger-button");
            m_headerShortcutBar.style.display = DisplayStyle.None;
            rootVisualElement.Add(m_headerShortcutBar);
        }

        Button AddHeaderShortcutButton(string text, string tooltip, Action action)
        {
            var button = new Button(action) { text = text, tooltip = tooltip };
            button.AddToClassList("mcp-button");
            button.AddToClassList("mcp-header-shortcut-button");
            button.style.display = DisplayStyle.None;
            m_headerShortcutBar.Add(button);
            return button;
        }

        void RegisterTab(ToolbarToggle toggle, string tabName)
        {
            toggle.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue)
                {
                    SelectTab(tabName);
                }
            });
        }

        void OnLanguageChanged(string displayName)
        {
            var language = McpWindowLocalization.LanguageCodeForDisplayName(displayName);
            if (string.Equals(language, McpWindowLocalization.CurrentLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var selectedTab = WindowState.SelectedTab;
            McpWindowLocalization.CurrentLanguage = language;
            CreateGUI();
            SelectTab(selectedTab);
            if (m_lastActionLabel != null)
            {
                m_lastActionLabel.text = McpWindowLocalization.Text("Language changed. MCP Window UI text has been refreshed.");
            }
        }

        VisualElement AddSection(VisualElement parent, string caption)
        {
            var section = new VisualElement();
            section.AddToClassList("mcp-section");
            var header = new Label(caption);
            header.AddToClassList("mcp-section-caption");
            var content = new VisualElement();
            content.AddToClassList("mcp-section-content");
            section.Add(header);
            section.Add(content);
            parent.Add(section);
            return content;
        }

        static string HeadingText(string text)
        {
            return string.IsNullOrEmpty(text) ? string.Empty : text.ToUpperInvariant();
        }

        VisualElement AddStatusLine(VisualElement parent, out Label label)
        {
            return StateRow(parent, out label);
        }

        VisualElement StateRow(VisualElement parent, out Label label)
        {
            var row = new VisualElement();
            row.AddToClassList("mcp-status-line");
            var indicator = new Label("●");
            indicator.AddToClassList("mcp-indicator");
            var statusLabel = new Label("Not checked.");
            statusLabel.AddToClassList("mcp-status-text");
            var copyButton = CopyButton(() => CopyText("Status", statusLabel.text), "Copy status");
            row.Add(indicator);
            row.Add(statusLabel);
            row.Add(copyButton);
            parent.Add(row);
            label = statusLabel;
            return indicator;
        }

        Label AddRow(VisualElement parent, string key, string value = "")
        {
            return InfoRow(parent, key, value);
        }

        Label AddActionRow(VisualElement parent, string key, string value, string buttonText, Action action, bool danger = false)
        {
            var row = new VisualElement();
            row.AddToClassList("mcp-row");
            row.AddToClassList("mcp-action-row");
            if (danger)
            {
                row.AddToClassList("mcp-danger-row");
            }

            var keyLabel = new Label(key);
            keyLabel.AddToClassList("mcp-key");
            var valueLabel = new Label(string.IsNullOrEmpty(value) ? "Not checked." : value);
            valueLabel.AddToClassList("mcp-value");
            var copyButton = CopyButton(() => CopyText(key, valueLabel.text), "Copy " + key);
            var button = new Button(action) { text = buttonText };
            button.AddToClassList("mcp-button");
            button.AddToClassList("mcp-inline-button");
            if (danger)
            {
                button.AddToClassList("mcp-danger-button");
            }

            row.Add(keyLabel);
            row.Add(valueLabel);
            row.Add(copyButton);
            row.Add(button);
            parent.Add(row);
            return valueLabel;
        }

        Label AddProfileRow(VisualElement parent, string key, string value, string applyButtonText, Action applyAction, string combinedButtonText, Action combinedAction, bool danger)
        {
            var row = new VisualElement();
            row.AddToClassList("mcp-row");
            row.AddToClassList("mcp-action-row");
            if (danger)
            {
                row.AddToClassList("mcp-danger-row");
            }

            var keyLabel = new Label(key);
            keyLabel.AddToClassList("mcp-key");
            var valueLabel = new Label(value);
            valueLabel.AddToClassList("mcp-value");
            var copyButton = CopyButton(() => CopyText(key, valueLabel.text), "Copy " + key);
            row.Add(keyLabel);
            row.Add(valueLabel);
            row.Add(copyButton);
            AddInlineButton(row, applyButtonText, applyAction, danger);
            if (!string.IsNullOrEmpty(combinedButtonText) && combinedAction != null)
            {
                AddInlineButton(row, combinedButtonText, combinedAction, danger);
            }

            parent.Add(row);
            return valueLabel;
        }

        void AddInlineButton(VisualElement parent, string text, Action action, bool danger)
        {
            var button = new Button(action) { text = text };
            button.AddToClassList("mcp-button");
            button.AddToClassList("mcp-inline-button");
            if (danger)
            {
                button.AddToClassList("mcp-danger-button");
            }

            parent.Add(button);
        }

        Label AddChecklistRow(VisualElement parent, string key, string importance, string nextAction)
        {
            var row = new VisualElement();
            row.AddToClassList("mcp-row");
            row.AddToClassList("mcp-checklist-row");
            var keyLabel = new Label(ChecklistKeyText(key, importance));
            keyLabel.AddToClassList("mcp-key");
            var field = new VisualElement();
            field.AddToClassList("mcp-checklist-field");
            var icon = new Label(ChecklistIcon("warn"));
            icon.AddToClassList("mcp-checklist-icon");
            var label = new Label(nextAction);
            label.AddToClassList("mcp-checklist-value");
            var copyButton = CopyButton(() => CopyText(key, label.text), "Copy " + key);
            field.Add(icon);
            field.Add(label);
            row.Add(keyLabel);
            row.Add(field);
            row.Add(copyButton);
            parent.Add(row);
            SetChecklistState(label, "warn");
            return label;
        }

        string ChecklistKeyText(string key, string importance)
        {
            var translatedKey = McpWindowLocalization.Text(key);
            return string.IsNullOrWhiteSpace(importance)
                ? translatedKey
                : translatedKey + " (" + McpWindowLocalization.Text(importance) + ")";
        }

        void HelpRow(VisualElement parent, string text)
        {
            var row = new Label(text);
            row.AddToClassList("mcp-help-row");
            parent.Add(row);
        }

        Label InfoRow(VisualElement parent, string key, string value = "")
        {
            var row = new VisualElement();
            row.AddToClassList("mcp-row");
            var keyLabel = new Label(key);
            keyLabel.AddToClassList("mcp-key");
            var valueLabel = new Label(string.IsNullOrEmpty(value) ? "Not checked." : value);
            valueLabel.AddToClassList("mcp-value");
            var copyButton = CopyButton(() => CopyText(key, valueLabel.text), "Copy " + key);
            row.Add(keyLabel);
            row.Add(valueLabel);
            row.Add(copyButton);
            parent.Add(row);
            return valueLabel;
        }

        TextField InputRow(VisualElement parent, string key, string value = "")
        {
            var field = new TextField(key) { value = value };
            field.AddToClassList("mcp-input-row");
            field.AddToClassList("mcp-input-field");
            parent.Add(field);
            return field;
        }

        TextField SecretInputRow(VisualElement parent, string key, string value = "")
        {
            var field = InputRow(parent, key, value);
            field.isPasswordField = true;
            field.AddToClassList("mcp-secret-row");
            return field;
        }

        Label OptionalToolRow(VisualElement parent, string key, string value = "")
        {
            var label = InfoRow(parent, key, value);
            label.AddToClassList("mcp-status-optional");
            return label;
        }

        VisualElement AddActions(VisualElement parent)
        {
            var actions = new VisualElement();
            actions.AddToClassList("mcp-actions");
            parent.Add(actions);
            return actions;
        }

        Button AddButton(VisualElement parent, string text, Action action)
        {
            var button = new Button(action) { text = text };
            button.AddToClassList("mcp-button");
            parent.Add(button);
            return button;
        }

        Button CopyButton(Action action, string tooltip)
        {
            var button = new Button(action) { text = string.Empty, tooltip = tooltip };
            button.AddToClassList("mcp-copy-button");
            var icon = new VisualElement();
            icon.AddToClassList("mcp-copy-icon");
            var back = new VisualElement();
            back.AddToClassList("mcp-copy-icon-back");
            var front = new VisualElement();
            front.AddToClassList("mcp-copy-icon-front");
            icon.Add(back);
            icon.Add(front);
            button.Add(icon);
            return button;
        }

        void CopyText(string key, string value)
        {
            GUIUtility.systemCopyBuffer = value ?? string.Empty;
            m_lastActionLabel.text = string.IsNullOrEmpty(key) ? "Copied to clipboard." : key + " copied to clipboard.";
        }




























        void SelectTab(string tabName)
        {
            if (string.Equals(tabName, "FTUE", StringComparison.Ordinal)
                && m_ftueTab != null
                && m_ftueTab.style.display.value == DisplayStyle.None)
            {
                tabName = "Setup";
            }

            tabName = WindowState.SelectTab(tabName);
            SelectTabState(m_ftueTab, m_ftuePanel, string.Equals(tabName, "FTUE", StringComparison.Ordinal));
            SelectTabState(m_setupTab, m_setupPanel, string.Equals(tabName, "Setup", StringComparison.Ordinal));
            SelectTabState(m_statusTab, m_statusPanel, string.Equals(tabName, "Status", StringComparison.Ordinal));
            SelectTabState(m_permissionsTab, m_permissionsPanel, string.Equals(tabName, "Permissions", StringComparison.Ordinal));
            SelectTabState(m_commandsTab, m_commandsPanel, string.Equals(tabName, "Commands", StringComparison.Ordinal));
            SelectTabState(m_plansTab, m_plansPanel, string.Equals(tabName, "Plans", StringComparison.Ordinal));
            SelectTabState(m_skillsTab, m_skillsPanel, string.Equals(tabName, "Skills", StringComparison.Ordinal));
            SelectTabState(m_unityAiAssistantTab, m_unityAiAssistantPanel, string.Equals(tabName, "Unity AI Assistant", StringComparison.Ordinal));
            SelectTabState(m_diagnosticsTab, m_diagnosticsPanel, string.Equals(tabName, "Diagnostics", StringComparison.Ordinal));
            if (string.Equals(tabName, "Setup", StringComparison.Ordinal))
            {
                TryStartAutomaticUnityToolFreshnessCheck();
            }
        }

        void SelectTabState(ToolbarToggle tab, VisualElement panel, bool selected)
        {
            if (tab != null)
            {
                tab.SetValueWithoutNotify(selected);
            }

            if (panel != null)
            {
                panel.style.display = selected ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        void RunAction(string title, Func<NodeRuntimeInstaller.InstallResult> action, bool probeHealthAfter = false)
        {
            try
            {
                var result = action();
                m_lastActionLabel.text = title + ": " + ShortActionMessage(result.Message);
                if (!result.Success)
                {
                    EditorUtility.DisplayDialog(title + " Failed", result.Message, "OK");
                }
            }
            catch (Exception exception)
            {
                m_lastActionLabel.text = title + ": " + ShortActionMessage(exception.Message);
                EditorUtility.DisplayDialog(title + " Failed", exception.Message, "OK");
            }
            finally
            {
                RefreshAll(probeHealthAfter);
            }
        }

        void RunGatewayProcessAction(string title, bool forceRestart, bool stopOnly)
        {
            if (s_gatewayProcessActionInProgress)
            {
                m_lastActionLabel.text = title + ": another Gateway process action is already in progress.";
                return;
            }

            GatewayInstaller.GatewayInstallStatus gatewayStatus = null;
            TaskScheduler mainThreadScheduler;
            try
            {
                if (!stopOnly)
                {
                    gatewayStatus = GatewayInstaller.GetStatus();
                }
                mainThreadScheduler = TaskScheduler.FromCurrentSynchronizationContext();
            }
            catch (Exception exception)
            {
                m_lastActionLabel.text = title + ": " + ShortActionMessage(exception.Message);
                EditorUtility.DisplayDialog(title + " Failed", exception.Message, "OK");
                return;
            }

            var windowsEditor = Application.platform == RuntimePlatform.WindowsEditor;
            var generation = ++m_gatewayProcessActionGeneration;
            s_gatewayProcessActionInProgress = true;
            SetGatewayProcessActionButtonsEnabled(false);
            m_lastActionLabel.text = title + ": in progress...";

            Task.Run(() => stopOnly
                    ? GatewayProcessManager.StopGateway(windowsEditor)
                    : GatewayProcessManager.StartOrRestartGateway(gatewayStatus, windowsEditor, forceRestart))
                .ContinueWith(task => CompleteGatewayProcessAction(title, generation, task), CancellationToken.None, TaskContinuationOptions.None, mainThreadScheduler);
        }

        void CompleteGatewayProcessAction(string title, int generation, Task<NodeRuntimeInstaller.InstallResult> task)
        {
            s_gatewayProcessActionInProgress = false;
            if (this == null || generation != m_gatewayProcessActionGeneration)
            {
                return;
            }

            SetGatewayProcessActionButtonsEnabled(true);
            var result = task.Status == TaskStatus.RanToCompletion
                ? task.Result
                : NodeRuntimeInstaller.InstallResult.Fail(task.IsCanceled ? "Gateway process action was cancelled." : task.Exception?.GetBaseException().Message ?? "Gateway process action failed.");
            m_lastActionLabel.text = title + ": " + ShortActionMessage(result.Message);
            if (!result.Success)
            {
                EditorUtility.DisplayDialog(title + " Failed", result.Message, "OK");
            }

            RefreshAll(true);
        }

        void SetGatewayProcessActionButtonsEnabled(bool enabled)
        {
            m_startGatewayShortcutButton?.SetEnabled(enabled);
            m_startGatewayButton?.SetEnabled(enabled);
            m_stopGatewayButton?.SetEnabled(enabled);
            m_ftueStartGatewayButton?.SetEnabled(enabled);
            for (var index = 0; index < m_restartGatewayButtons.Count; index++)
            {
                m_restartGatewayButtons[index]?.SetEnabled(enabled);
            }
        }

        string ShortActionMessage(string message)
        {
            var text = string.IsNullOrWhiteSpace(message) ? "No output" : message.Trim().Replace("\r", string.Empty);
            var firstLineEnd = text.IndexOf('\n');
            if (firstLineEnd >= 0)
            {
                text = text.Substring(0, firstLineEnd).Trim() + " ...";
            }

            return Bounded(text, 360);
        }

        void RefreshAll(bool probeHealth = false)
        {
            m_refreshFailures.Clear();
            UpdateRefreshFailureBanner();
            var details = new StringBuilder();
            var total = Stopwatch.StartNew();
            using (s_mcpWindowRefreshMarker.Auto())
            {
                TrackMcpWindowStep(details, "node", RefreshNode);
                TrackMcpWindowStep(details, "heartbeat", s_mcpWindowHeartbeatMarker, () => ForceHeartbeatBeforeHealthProbe(probeHealth));
                GatewayProcessManager.GatewayRuntimeStatus processStatus = null;
                TrackMcpWindowStep(details, "health", s_mcpWindowHealthCheckMarker, () => processStatus = RefreshProcess(probeHealth));
                TrackMcpWindowStep(details, "gateway", () => RefreshGateway(processStatus));
                TrackMcpWindowStep(details, "bridge", RefreshBridge);
                TrackMcpWindowStep(details, "catalog", s_mcpWindowCatalogRefreshMarker, RefreshCommands);
                TrackMcpWindowStep(details, "setup_checklist", () => RefreshSetupChecklist(processStatus));
                TrackMcpWindowStep(details, "canonical_path_registry", RefreshCanonicalPathRegistry);
                TrackMcpWindowStep(details, "mcp_token_setup", RefreshMcpTokenSetup);
                GatewayNetworkModeManager.NetworkStatus networkStatus = null;
                TrackMcpWindowStep(details, "mcp_connection", () => networkStatus = RefreshMcpConnection());
                TrackMcpWindowStep(details, "mcp_client_config", () => RefreshMcpClientConfig(networkStatus));
                TrackMcpWindowStep(details, "setup_preferences", RefreshSetupPreferences);
                TrackMcpWindowStep(details, "optional_tools_setup", RefreshOptionalToolsSetup);
                TrackMcpWindowStep(details, "permissions", RefreshPermissions);
                TrackMcpWindowStep(details, "ftue", () => RefreshFtue(processStatus, networkStatus));
                TrackMcpWindowStep(details, "plans", RefreshPlans);
                TrackMcpWindowStep(details, "skills", RefreshSkills);
                TrackMcpWindowStep(details, "unity_ai_assistant", RefreshUnityAiAssistant);
                TrackMcpWindowStep(details, "diagnostics", RefreshDiagnostics);
                TrackMcpWindowStep(details, "header_shortcuts", () => RefreshHeaderShortcuts(null, processStatus, null));
                TrackMcpWindowStep(details, "localization", ApplyLocalization);
            }

            total.Stop();
            SetMcpWindowTimingSummary("RefreshAll", total.ElapsedMilliseconds, details);
            if (m_refreshFailures.Count > 0 && m_lastActionLabel != null)
            {
                m_lastActionLabel.text = McpWindowLocalization.Text("Some read-only checks failed. Other sections were refreshed.") + " " + string.Join("; ", m_refreshFailures);
            }
        }


        internal string CurrentTabForCommand()
        {
            return WindowState.SelectedTab;
        }

        internal string LatestTimingSummaryForCommand()
        {
            return McpWindowTimingViewModel.LatestSummary(m_lastMcpWindowTimingSummary);
        }

        internal GatewayProcessManager.GatewayRuntimeStatus LastHealthProbeForCommand()
        {
            return m_lastProbedProcessStatus;
        }

        internal string LastHealthProbeMessageForCommand()
        {
            return Bounded(m_lastProbedProcessStatus?.Message ?? string.Empty, 240);
        }

        internal bool IsGuiReadyForCommand()
        {
            return rootVisualElement != null && m_mainScrollView != null;
        }

        internal bool HasMainScrollViewForCommand()
        {
            return m_mainScrollView != null;
        }

        internal bool HealthRefreshShortcutVisibleForCommand()
        {
            return HeaderShortcutVisible(m_refreshHealthShortcutButton);
        }

        internal bool HeaderShortcutBarVisibleForCommand()
        {
            return m_headerShortcutBar != null && m_headerShortcutBar.style.display.value == DisplayStyle.Flex;
        }

        internal Vector2 MainScrollOffsetForCommand()
        {
            return m_mainScrollView == null ? Vector2.zero : m_mainScrollView.scrollOffset;
        }

        internal void SelectTabForCommand(string tabName)
        {
            if (!string.IsNullOrWhiteSpace(tabName))
            {
                SelectTab(tabName);
            }
        }

        internal string RefreshAllForCommand(bool probeHealth)
        {
            RefreshAll(probeHealth);
            return LatestTimingSummaryForCommand();
        }

        internal string RefreshHealthForCommand()
        {
            RefreshHealthAndChecklist();
            return LatestTimingSummaryForCommand();
        }

        internal McpWindowScrollBenchmarkResult RunScrollBenchmarkForCommand(int steps, float deltaPixels, bool restoreScroll, bool refreshDiagnosticsEachStep)
        {
            var result = new McpWindowScrollBenchmarkResult
            {
                Available = m_mainScrollView != null,
                Reason = m_mainScrollView == null ? "main ScrollView is not available; open the MCP Window first" : string.Empty,
                StepsRequested = steps,
                DeltaPixels = deltaPixels,
                Restored = restoreScroll,
                RefreshDiagnosticsEachStep = refreshDiagnosticsEachStep,
                RepaintRequested = false,
                TimingSummary = LatestTimingSummaryForCommand(),
            };

            if (m_mainScrollView == null || steps <= 0)
            {
                return result;
            }

            var original = m_mainScrollView.scrollOffset;
            var currentY = original.y;
            result.OriginalScrollY = original.y;
            var total = Stopwatch.StartNew();
            long minStepMs = long.MaxValue;
            long maxStepMs = 0;
            long sumStepMs = 0;
            var details = new StringBuilder();

            for (var index = 0; index < steps; index++)
            {
                var step = Stopwatch.StartNew();
                var direction = index % 2 == 0 ? 1f : -1f;
                currentY = Math.Max(0f, currentY + deltaPixels * direction);
                m_mainScrollView.scrollOffset = new Vector2(original.x, currentY);
                if (refreshDiagnosticsEachStep)
                {
                    RefreshDiagnosticsTiming();
                }

                m_mainScrollView.MarkDirtyRepaint();
                if (rootVisualElement != null)
                {
                    rootVisualElement.MarkDirtyRepaint();
                }

                Repaint();
                step.Stop();

                var elapsed = step.ElapsedMilliseconds;
                minStepMs = Math.Min(minStepMs, elapsed);
                maxStepMs = Math.Max(maxStepMs, elapsed);
                sumStepMs += elapsed;
                result.StepsCompleted++;
            }

            if (restoreScroll)
            {
                m_mainScrollView.scrollOffset = original;
                currentY = original.y;
            }

            total.Stop();
            result.TotalMs = total.ElapsedMilliseconds;
            result.MinStepMs = result.StepsCompleted == 0 ? 0 : minStepMs;
            result.MaxStepMs = result.StepsCompleted == 0 ? 0 : maxStepMs;
            result.AverageStepMs = result.StepsCompleted == 0 ? 0 : (double)sumStepMs / result.StepsCompleted;
            result.FinalScrollY = currentY;
            result.RepaintRequested = true;
            details.Append("steps=").Append(result.StepsCompleted);
            details.Append("; min_step=").Append(result.MinStepMs).Append("ms");
            details.Append("; max_step=").Append(result.MaxStepMs).Append("ms");
            SetMcpWindowTimingSummary("ScrollBenchmark", result.TotalMs, details);
            result.TimingSummary = LatestTimingSummaryForCommand();
            return result;
        }

        void ForceHeartbeatBeforeHealthProbe(bool probeHealth)
        {
            if (probeHealth)
            {
                using (s_mcpWindowHeartbeatMarker.Auto())
                {
                    UnityBridgeRuntime.ForceHeartbeat("refresh_health");
                }
            }
        }

        void ScheduleWindowRefreshAfterHeartbeat()
        {
            m_nextHeartbeatWindowRefreshTime = EditorApplication.timeSinceStartup + HeartbeatWindowRefreshDelaySeconds;
            EditorApplication.update -= RefreshWindowAfterHeartbeat;
            EditorApplication.update += RefreshWindowAfterHeartbeat;
        }

        void RefreshWindowAfterHeartbeat()
        {
            if (EditorApplication.timeSinceStartup < m_nextHeartbeatWindowRefreshTime)
            {
                return;
            }

            EditorApplication.update -= RefreshWindowAfterHeartbeat;
            m_nextHeartbeatWindowRefreshTime = -1.0;
            var details = new StringBuilder();
            var total = Stopwatch.StartNew();
            using (s_mcpWindowHeartbeatMarker.Auto())
            {
                RefreshHeartbeatViews(details);
            }

            total.Stop();
            SetMcpWindowTimingSummary("HeartbeatRefresh", total.ElapsedMilliseconds, details);
        }

        void RefreshHeartbeatViews(StringBuilder details)
        {
            TrackMcpWindowStep(details, "heartbeat_views.bridge", RefreshBridge);
            RefreshUnityBridgeChecklist();
            RefreshCatalogPolicySummaryAfterHeartbeat();
            RefreshHeartbeatHealthActions();
            RefreshFtueAfterHeartbeat();
        }

        void RefreshHeartbeatDependentViews(GatewayProcessManager.GatewayRuntimeStatus processStatus, StringBuilder details, string prefix)
        {
            TrackMcpWindowStep(details, prefix + "gateway", () => RefreshGateway(processStatus));
            TrackMcpWindowStep(details, prefix + "bridge", RefreshBridge);
            TrackMcpWindowStep(details, prefix + "setup_checklist", () => RefreshSetupChecklist(processStatus));
            TrackMcpWindowStep(details, prefix + "canonical_path_registry", RefreshCanonicalPathRegistry);
            TrackMcpWindowStep(details, prefix + "diagnostics", RefreshDiagnostics);
            TrackMcpWindowStep(details, prefix + "header_shortcuts", () => RefreshHeaderShortcuts(null, processStatus, null));
            TrackMcpWindowStep(details, prefix + "localization", ApplyLocalization);
        }

        void TrackMcpWindowStep(StringBuilder builder, string stepName, Action action)
        {
            if (builder == null)
            {
                action();
                return;
            }

            var step = Stopwatch.StartNew();
            try
            {
                action();
            }
            catch (Exception exception)
            {
                var failure = stepName + ": " + Bounded(exception.Message, 200);
                m_refreshFailures.Add(failure);
                UpdateRefreshFailureBanner();
                builder.Append(builder.Length > 0 ? "; " : string.Empty).Append(stepName).Append("=failed");
                if (m_lastActionLabel != null) m_lastActionLabel.text = McpWindowLocalization.Text("Read-only check failed:") + " " + failure;
                Debug.LogWarning("SGG Unity MCP Window read-only refresh failed: " + failure);
            }
            finally
            {
                step.Stop();
                AppendMcpWindowTiming(builder, stepName, step.ElapsedMilliseconds);
            }
        }

        void UpdateRefreshFailureBanner()
        {
            if (m_refreshFailureLabel == null) return;
            m_refreshFailureLabel.style.display = m_refreshFailures.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            m_refreshFailureLabel.text = m_refreshFailures.Count == 0 ? string.Empty
                : McpWindowLocalization.Text("Some read-only checks failed. Other sections were refreshed.") + " " + string.Join("; ", m_refreshFailures);
        }

        void TrackMcpWindowStep(StringBuilder builder, string stepName, ProfilerMarker marker, Action action)
        {
            using (marker.Auto())
            {
                TrackMcpWindowStep(builder, stepName, action);
            }
        }

        static void AppendMcpWindowTiming(StringBuilder builder, string stepName, long durationMs)
        {
            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder.Append(stepName);
            builder.Append('=');
            builder.Append(durationMs);
            builder.Append("ms");
        }

        void SetMcpWindowTimingSummary(string source, long totalMs, StringBuilder details)
        {
            m_lastMcpWindowTimingSummary = McpWindowTimingViewModel.BuildSummary(source, totalMs, details == null ? null : details.ToString());
            RefreshDiagnosticsTiming();
        }

        void RefreshDiagnosticsTiming()
        {
            if (m_diagnosticsWindowTiming != null)
            {
                m_diagnosticsWindowTiming.text = m_lastMcpWindowTimingSummary;
            }
        }

        void ApplyLocalization()
        {
            var language = McpWindowLocalization.CurrentLanguage;
            var defaultLanguage = string.Equals(language, McpWindowLocalization.DefaultLanguage, StringComparison.OrdinalIgnoreCase);
            if (defaultLanguage && !m_localizationDirty && string.Equals(m_lastAppliedLocalizationLanguage, language, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            McpWindowLocalization.ApplyTo(rootVisualElement);
            ApplyHeadingCase(rootVisualElement);
            m_lastAppliedLocalizationLanguage = language;
            m_localizationDirty = false;
        }

        static void ApplyHeadingCase(VisualElement element)
        {
            if (element == null)
            {
                return;
            }

            var headingLabel = element as Label;
            if (headingLabel != null && (element.ClassListContains("mcp-title") || element.ClassListContains("mcp-section-caption")))
            {
                headingLabel.text = HeadingText(headingLabel.text);
            }

            var tab = element as ToolbarToggle;
            if (tab != null && element.ClassListContains("mcp-tab"))
            {
                tab.text = HeadingText(tab.text);
            }

            foreach (var child in element.Children())
            {
                ApplyHeadingCase(child);
            }
        }

        void RestartGateway()
        {
            RunGatewayProcessAction("Restart Gateway", true, false);
        }

        void StopGateway()
        {
            RunGatewayProcessAction("Stop Gateway", false, true);
        }


        void UpdateGatewayHealthLabels(VisualElement indicator, Label statusLabel, Label healthLabel, Label bodyLabel, Label logPathLabel, Label logTailLabel, GatewayProcessManager.GatewayRuntimeStatus status, GatewayInstaller.GatewayInstallStatus gatewayStatus)
        {
            if (statusLabel == null)
            {
                return;
            }

            statusLabel.text = GatewayProcessStatusText(status, gatewayStatus);
            if (healthLabel != null)
            {
                healthLabel.text = status.HealthUrl;
            }

            if (bodyLabel != null)
            {
                bodyLabel.text = status.HealthSummary;
            }

            var policySummary = McpRuntimePolicyViewModel.BuildSummary(status);
            if (m_statusRuntimePolicy != null)
            {
                m_statusRuntimePolicy.text = policySummary;
            }
            if (m_permissionsRuntimePolicy != null)
            {
                m_permissionsRuntimePolicy.text = policySummary;
            }

            if (logPathLabel != null)
            {
                logPathLabel.text = string.IsNullOrEmpty(status.LogPath) ? status.LogDirectory : status.LogPath;
            }

            if (logTailLabel != null)
            {
                logTailLabel.text = string.IsNullOrEmpty(status.LogTail) ? "No gateway log output" : status.LogTail;
            }

            SetIndicator(indicator, McpRuntimeHealthViewState.Evaluate(status, gatewayStatus).CurrentHealthyRuntime, status.HealthOk || status.ProcessAlive);
        }


        internal static void SetActionNeeded(Button button, bool needed, string tooltip)
        {
            if (button == null)
            {
                return;
            }

            if (needed)
            {
                button.AddToClassList("mcp-action-needed");
                button.tooltip = tooltip ?? string.Empty;
            }
            else
            {
                button.RemoveFromClassList("mcp-action-needed");
                button.tooltip = string.Empty;
            }
        }


















        static string SafeReadAllText(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return string.Empty;
            }

            try
            {
                return File.ReadAllText(path);
            }
            catch
            {
                return string.Empty;
            }
        }

        string Bounded(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            {
                return value;
            }

            return value.Substring(0, maxChars) + "...";
        }

        string ReadFeatureFlagsSummary(string configPath)
        {
            if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
            {
                return "Config missing";
            }

            var source = File.ReadAllText(configPath);
            var enabled = 0;
            var disabled = 0;
            foreach (Match match in Regex.Matches(source, "\\\"([A-Za-z0-9_]+)\\\"\\s*:\\s*(true|false)"))
            {
                var name = match.Groups[1].Value;
                if (name == "audit_enabled" || name == "allow_non_loopback")
                {
                    continue;
                }

                if (match.Groups[2].Value == "true")
                {
                    enabled++;
                }
                else
                {
                    disabled++;
                }
            }

            return enabled + " enabled, " + disabled + " disabled; diagnostics flag " + (source.Contains("\"sgg_mcp_diagnostics\"") ? "present" : "missing")
                + ", plans flag " + (source.Contains("\"sgg_mcp_plans\"") ? "present" : "missing")
                + ", skills flag " + (source.Contains("\"sgg_mcp_skills\"") ? "present" : "missing")
                + ", checkpoints flag " + (source.Contains("\"sgg_mcp_checkpoints\"") ? "present" : "missing")
                + ", artifacts flag " + (source.Contains("\"sgg_mcp_artifacts\"") ? "present" : "missing")
                + ", policy flag " + (source.Contains("\"sgg_mcp_policy\"") ? "present" : "missing");
        }

        string RegisteredPackageSummary(string packageName)
        {
            var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
            for (var index = 0; index < packages.Length; index++)
            {
                if (string.Equals(packages[index].name, packageName, StringComparison.Ordinal))
                {
                    return packages[index].version + " from " + packages[index].source;
                }
            }

            return string.Empty;
        }

        string CanonicalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            if (!Path.IsPathRooted(path) && !Regex.IsMatch(path, "^[A-Za-z]:[\\\\/]"))
            {
                return path;
            }

            try
            {
                return new CanonicalPathService().Normalize(path).Value;
            }
            catch
            {
                return path;
            }
        }


        internal static string FormatTokenPresence(string token)
        {
            return string.IsNullOrEmpty(token) ? "Not configured" : "Present";
        }

        string FormatAge(TimeSpan age)
        {
            if (age.TotalSeconds < 0)
            {
                age = TimeSpan.Zero;
            }

            if (age.TotalMinutes < 1)
            {
                return Math.Floor(age.TotalSeconds) + " seconds";
            }

            if (age.TotalHours < 1)
            {
                return Math.Floor(age.TotalMinutes) + " minutes";
            }

            return Math.Floor(age.TotalHours) + " hours";
        }

        string ProjectManifestPath()
        {
            return Path.Combine(ProjectPaths.ProjectRoot, "Packages", "manifest.json");
        }

        string ReadUnityPackageVersion()
        {
            return ReadJsonStringField(UnityPackageManifestPath(), "version", "Unknown");
        }

        string ReadProjectManifestDependencyVersion(string packageName)
        {
            var manifestPath = ProjectManifestPath();
            if (!File.Exists(manifestPath))
            {
                return string.Empty;
            }

            var source = File.ReadAllText(manifestPath);
            var dependencyMarker = "\"" + packageName + "\"";
            var dependencyIndex = source.IndexOf(dependencyMarker, StringComparison.Ordinal);
            if (dependencyIndex < 0)
            {
                return string.Empty;
            }

            var colonIndex = source.IndexOf(':', dependencyIndex);
            return colonIndex < 0 ? string.Empty : ReadJsonStringValueAt(source, colonIndex + 1, string.Empty);
        }

        string ReadJsonStringField(string filePath, string fieldName, string fallback)
        {
            if (!File.Exists(filePath))
            {
                return fallback;
            }

            var source = File.ReadAllText(filePath);
            var marker = "\"" + fieldName + "\"";
            var fieldIndex = source.IndexOf(marker, StringComparison.Ordinal);
            if (fieldIndex < 0)
            {
                return fallback;
            }

            var colonIndex = source.IndexOf(':', fieldIndex);
            return colonIndex < 0 ? fallback : ReadJsonStringValueAt(source, colonIndex + 1, fallback);
        }

        string ReadJsonStringValueAt(string source, int startIndex, string fallback)
        {
            var quoteStart = source.IndexOf('"', startIndex);
            if (quoteStart < 0)
            {
                return fallback;
            }

            var quoteEnd = source.IndexOf('"', quoteStart + 1);
            return quoteEnd < 0 ? fallback : source.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
        }

        string UnityPackageManifestPath()
        {
            var packagePath = UnityMcpPackagePaths.PackageResolvedPath;
            if (string.IsNullOrEmpty(packagePath))
            {
                packagePath = UnityMcpPackagePaths.PackageAssetPath;
            }

            return Path.Combine(packagePath, "package.json");
        }

        void SetIndicator(VisualElement indicator, bool ok, bool warn)
        {
            if (indicator == null)
            {
                return;
            }

            indicator.RemoveFromClassList("mcp-indicator--ok");
            indicator.RemoveFromClassList("mcp-indicator--warn");
            indicator.RemoveFromClassList("mcp-indicator--error");
            var label = indicator as Label;
            if (ok)
            {
                indicator.AddToClassList("mcp-indicator--ok");
                if (label != null)
                {
                    label.text = "✔";
                    label.tooltip = "Active/Success";
                }
            }
            else if (warn)
            {
                indicator.AddToClassList("mcp-indicator--warn");
                if (label != null)
                {
                    label.text = "▲";
                    label.tooltip = "Warning/Caution";
                }
            }
            else
            {
                indicator.AddToClassList("mcp-indicator--error");
                if (label != null)
                {
                    label.text = "●";
                    label.tooltip = "Error/Danger";
                }
            }
        }

    }
}
