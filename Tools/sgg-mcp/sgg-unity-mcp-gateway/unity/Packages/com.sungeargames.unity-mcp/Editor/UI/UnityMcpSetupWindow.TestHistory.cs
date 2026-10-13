using System;
using System.Threading.Tasks;
using SGG.UnityMCP.Editor.Setup;
using SGG.UnityMCP.Editor.UI.Localization;
using UnityEditor;
using UnityEngine.UIElements;

namespace SGG.UnityMCP.Editor.UI
{
    public sealed partial class UnityMcpSetupWindow
    {
        Label m_historyStatus;
        ProgressBar m_historyBytes;
        ProgressBar m_historyBindings;
        IntegerField m_historySizeMiB;
        IntegerField m_historyRecordLimit;
        VisualElement m_historySettings;
        Button m_historyRefreshButton;
        Button m_historySettingsButton;
        Button m_historyClearButton;
        Button m_historySaveButton;
        Button m_historyDefaultsButton;
        GatewayTestHistoryModel.Snapshot m_historySnapshot;
        bool m_historyBusy;
        bool m_historySettingsDirty;
        bool m_historyByteLimitDirty;
        int m_historyUiGeneration;
        static int s_historyInFlight;
        static event Action HistoryBusyChanged;
        internal Func<GatewayTestHistoryManager.Operation, GatewayTestHistoryModel.Snapshot, int, int, bool, bool, Task<GatewayTestHistoryManager.Result>> HistoryOperationForTests;
        internal Func<string, string, string, string, bool> HistoryConfirmationForTests;

        void BuildTestHistorySection(VisualElement parent)
        {
            m_historyUiGeneration++;
            m_historySnapshot = null;
            m_historySettingsDirty = false;
            m_historyByteLimitDirty = false;
            var section = AddSection(parent, "Gateway Test History");
            section.name = "setup-test-history";
            HelpRow(section, "This is Gateway owner-binding metadata, not Unity test result artifacts or an MCP protocol limit. Byte capacity and record capacity are independent.");
            AddRow(section, "Default limits", "200 MiB; 192000 records.");
            HelpRow(section, "The bars use saved config limits. Restart Gateway after saving to apply them; the running Gateway limits are not verified by this local check.");
            m_historyStatus = AddRow(section, "History status", "Not checked. Refresh History reads metadata only.");
            m_historyStatus.name = "setup-test-history-status";
            m_historyBytes = new ProgressBar { name = "setup-test-history-bytes", lowValue = 0, highValue = 100 };
            m_historyBindings = new ProgressBar { name = "setup-test-history-bindings", lowValue = 0, highValue = 100 };
            m_historyBytes.AddToClassList("mcp-history-progress");
            m_historyBindings.AddToClassList("mcp-history-progress");
            section.Add(m_historyBytes);
            section.Add(m_historyBindings);
            SetHistoryUsageUnavailable();
            var actions = AddActions(section);
            m_historyRefreshButton = AddButton(actions, "Refresh History", () => _ = RefreshTestHistoryAsync());
            m_historyRefreshButton.name = "setup-test-history-refresh";
            m_historySettingsButton = AddButton(actions, "Storage Settings", () => m_historySettings.style.display = m_historySettings.style.display.value == DisplayStyle.None ? DisplayStyle.Flex : DisplayStyle.None);
            m_historySettingsButton.name = "setup-test-history-settings";
            m_historyClearButton = AddButton(actions, "Clear Finished History", () => _ = ClearTestHistoryAsync());
            m_historyClearButton.name = "setup-test-history-clear";
            m_historyClearButton.AddToClassList("mcp-danger-button");
            HelpRow(section, "Cleanup always previews first and requires confirmation. Active, unknown, foreign, invalid and changing records are preserved. A changed preview is refused; cleanup is never retried automatically.");
            HelpRow(section, "Each confirmed cleanup removes at most 256 validated records. Repeat only after a new preview if more finished history remains.");

            m_historySettings = new VisualElement { name = "setup-test-history-settings-panel" };
            m_historySettings.AddToClassList("mcp-history-settings");
            m_historySettings.style.display = DisplayStyle.None;
            section.Add(m_historySettings);
            m_historySizeMiB = new IntegerField("Capacity (MiB)") { name = "setup-test-history-size-mib", value = GatewayTestHistoryModel.DefaultMiB, isDelayed = true };
            m_historyRecordLimit = new IntegerField("Record limit") { name = "setup-test-history-record-limit", value = GatewayTestHistoryModel.DefaultBindings, isDelayed = true };
            m_historySizeMiB.AddToClassList("mcp-input-row");
            m_historyRecordLimit.AddToClassList("mcp-input-row");
            m_historySettings.Add(m_historySizeMiB);
            m_historySettings.Add(m_historyRecordLimit);
            m_historySizeMiB.RegisterValueChangedCallback(_ => { m_historyByteLimitDirty = true; m_historySettingsDirty = true; UpdateHistoryButtons(); });
            m_historyRecordLimit.RegisterValueChangedCallback(_ => { m_historySettingsDirty = true; UpdateHistoryButtons(); });
            HelpRow(m_historySettings, "Allowed: 1–1024 MiB and 1–1000000 records. Reducing a limit does not delete history; new runs may be refused until usage fits. Saving does not restart Gateway or change permissions.");
            var settingsActions = AddActions(m_historySettings);
            m_historySaveButton = AddButton(settingsActions, "Save Storage Limits", () => _ = SaveTestHistoryLimitsAsync());
            m_historySaveButton.name = "setup-test-history-save";
            m_historyDefaultsButton = AddButton(settingsActions, "Use Default Limits", () =>
            {
                m_historySizeMiB.SetValueWithoutNotify(GatewayTestHistoryModel.DefaultMiB);
                m_historyRecordLimit.SetValueWithoutNotify(GatewayTestHistoryModel.DefaultBindings);
                m_historyByteLimitDirty = m_historySettingsDirty = true;
                UpdateHistoryButtons();
            });
            HistoryBusyChanged -= UpdateHistoryButtons;
            HistoryBusyChanged += UpdateHistoryButtons;
            UpdateHistoryButtons();
        }

        void SetHistoryUsageUnavailable()
        {
            m_historyBytes.value = m_historyBindings.value = 0;
            m_historyBytes.title = McpWindowLocalization.Text("Metadata usage unknown — refresh history");
            m_historyBindings.title = McpWindowLocalization.Text("Record usage unknown — refresh history");
            m_historyBytes.SetEnabled(false);
            m_historyBindings.SetEnabled(false);
            m_historyBytes.RemoveFromClassList("mcp-history-full");
            m_historyBindings.RemoveFromClassList("mcp-history-full");
        }

        internal void RenderTestHistory(GatewayTestHistoryModel.Snapshot snapshot)
        {
            if (!GatewayTestHistoryModel.ValidSnapshot(snapshot))
            {
                m_historySnapshot = null;
                SetHistoryUsageUnavailable();
                m_historyStatus.text = McpWindowLocalization.Text("History status unavailable. Nothing was removed.");
            }
            else
            {
                m_historySnapshot = snapshot;
                m_historyBytes.SetEnabled(true);
                m_historyBindings.SetEnabled(true);
                m_historyBytes.value = GatewayTestHistoryModel.Percent(snapshot.bytes, snapshot.max_bytes);
                m_historyBindings.value = GatewayTestHistoryModel.Percent(snapshot.bindings, snapshot.max_bindings);
                m_historyBytes.title = GatewayTestHistoryModel.ByteUsage(snapshot);
                m_historyBindings.title = GatewayTestHistoryModel.BindingUsage(snapshot);
                m_historyStatus.text = McpWindowLocalization.Format("Saved limits; retention {0} days. Running limits not verified.", snapshot.retention_days);
                m_historyBytes.EnableInClassList("mcp-history-full", snapshot.bytes >= snapshot.max_bytes);
                m_historyBindings.EnableInClassList("mcp-history-full", snapshot.bindings >= snapshot.max_bindings);
                if (!m_historySettingsDirty)
                {
                    // A non-integral saved byte limit must not be silently rounded and saved back.
                    m_historySizeMiB.SetValueWithoutNotify((int)Math.Ceiling(snapshot.max_bytes / (double)GatewayTestHistoryModel.MiB));
                    m_historyRecordLimit.SetValueWithoutNotify(snapshot.max_bindings);
                }
            }
            UpdateHistoryButtons();
        }

        void UpdateHistoryButtons()
        {
            var busy = m_historyBusy || s_historyInFlight > 0;
            m_historyRefreshButton?.SetEnabled(!busy);
            m_historySettingsButton?.SetEnabled(!busy);
            m_historyClearButton?.SetEnabled(!busy && GatewayTestHistoryModel.ValidSnapshot(m_historySnapshot));
            m_historySizeMiB?.SetEnabled(!busy);
            m_historyRecordLimit?.SetEnabled(!busy);
            m_historyDefaultsButton?.SetEnabled(!busy);
            m_historySaveButton?.SetEnabled(!busy && m_historySettingsDirty && m_historySnapshot != null
                && GatewayTestHistoryModel.IsFingerprint(m_historySnapshot.config_fingerprint)
                && GatewayTestHistoryModel.ValidLimits(m_historySizeMiB.value, m_historyRecordLimit.value));
        }

        Task<GatewayTestHistoryManager.Result> RunHistoryOperation(GatewayTestHistoryManager.Target target, GatewayTestHistoryManager.Operation operation,
            GatewayTestHistoryModel.Snapshot binding = null, int sizeMiB = GatewayTestHistoryModel.DefaultMiB,
            int recordLimit = GatewayTestHistoryModel.DefaultBindings, bool confirmed = false, bool preserveByteLimit = false)
        {
            if (HistoryOperationForTests != null) return HistoryOperationForTests(operation, binding, sizeMiB, recordLimit, confirmed, preserveByteLimit);
            return Task.Run(() => GatewayTestHistoryManager.Run(target, operation, binding, sizeMiB, recordLimit, confirmed, preserveByteLimit));
        }

        bool BeginHistoryOperation()
        {
            if (m_historyBusy || s_historyInFlight > 0) return false;
            m_historyBusy = true;
            s_historyInFlight++;
            HistoryBusyChanged?.Invoke();
            return true;
        }

        void EndHistoryOperation(int generation)
        {
            m_historyBusy = false;
            s_historyInFlight--;
            if (this != null && generation != m_historyUiGeneration && m_historyStatus != null)
                m_historyStatus.text = McpWindowLocalization.Text("History action finished after the view changed. Refresh History before another action.");
            HistoryBusyChanged?.Invoke();
        }

        GatewayTestHistoryManager.Target HistoryTarget()
        {
            return HistoryOperationForTests == null ? GatewayTestHistoryManager.CaptureTarget() : null;
        }

        bool ConfirmHistory(string title, string message, string accept)
        {
            return HistoryConfirmationForTests != null ? HistoryConfirmationForTests(title, message, accept, "Cancel")
                : EditorUtility.DisplayDialog(McpWindowLocalization.Text(title), message, McpWindowLocalization.Text(accept), McpWindowLocalization.Text("Cancel"));
        }

        void ShowHistoryFailure(GatewayTestHistoryManager.Result result)
        {
            RenderTestHistory(null);
            m_historyStatus.text = McpWindowLocalization.Text("History action failed or its outcome is uncertain. Refresh before another action; do not retry cleanup automatically.")
                + " " + Bounded(result?.error?.code ?? "admin_outcome_uncertain", 100);
        }

        internal async Task RefreshTestHistoryAsync()
        {
            if (!BeginHistoryOperation()) return;
            var generation = m_historyUiGeneration;
            m_historyStatus.text = McpWindowLocalization.Text("Reading history metadata...");
            try
            {
                var result = await RunHistoryOperation(HistoryTarget(), GatewayTestHistoryManager.Operation.Status);
                if (this == null || generation != m_historyUiGeneration) return;
                if (result.Success) RenderTestHistory(result.data); else ShowHistoryFailure(result);
            }
            catch { if (this != null && generation == m_historyUiGeneration) ShowHistoryFailure(null); }
            finally { EndHistoryOperation(generation); }
        }

        internal async Task ClearTestHistoryAsync()
        {
            if (!BeginHistoryOperation()) return;
            var generation = m_historyUiGeneration;
            try
            {
                var target = HistoryTarget();
                var preview = await RunHistoryOperation(target, GatewayTestHistoryManager.Operation.PreviewCleanup);
                if (this == null || generation != m_historyUiGeneration) return;
                if (!preview.Success) { ShowHistoryFailure(preview); return; }
                RenderTestHistory(preview.data);
                if (!GatewayTestHistoryModel.CanConfirmCleanup(preview.data, DateTime.UtcNow))
                {
                    m_historyStatus.text = McpWindowLocalization.Text("No safely removable history was found. Nothing was removed.");
                    if (!string.IsNullOrEmpty(preview.data.completed_cleanup_blocked))
                        m_historyStatus.text += " " + McpWindowLocalization.Text("Completed-test cleanup is unavailable; this preview includes only proven non-executed requests.");
                    return;
                }
                if (!ConfirmHistory("Clear Finished History", GatewayTestHistoryModel.CleanupSummary(preview.data), "Clear History"))
                { m_historyStatus.text = McpWindowLocalization.Text("Cleanup cancelled. Nothing was removed."); return; }
                var result = await RunHistoryOperation(target, GatewayTestHistoryManager.Operation.Cleanup, preview.data, confirmed: true);
                if (this == null || generation != m_historyUiGeneration) return;
                if (!result.Success || result.data.removed == null) { ShowHistoryFailure(result); return; }
                RenderTestHistory(result.data);
                m_historyStatus.text = McpWindowLocalization.Format("Removed {0} history records. Unity results and reports were preserved.", result.data.removed.bindings);
            }
            catch { if (this != null && generation == m_historyUiGeneration) ShowHistoryFailure(null); }
            finally { EndHistoryOperation(generation); }
        }

        internal async Task SaveTestHistoryLimitsAsync()
        {
            var size = m_historySizeMiB.value;
            var records = m_historyRecordLimit.value;
            if (m_historyBusy || s_historyInFlight > 0 || !m_historySettingsDirty || !GatewayTestHistoryModel.ValidLimits(size, records)
                || m_historySnapshot == null || !GatewayTestHistoryModel.IsFingerprint(m_historySnapshot.config_fingerprint)) return;
            var sizeLabel = m_historyByteLimitDirty ? size.ToString() : (m_historySnapshot.max_bytes / (double)GatewayTestHistoryModel.MiB).ToString("0.##########", System.Globalization.CultureInfo.InvariantCulture);
            if (!ConfirmHistory("Save Storage Limits", McpWindowLocalization.Format("Save {0} MiB and {1} records? Restart Gateway to apply. Existing history is not deleted; permissions are unchanged.", sizeLabel, records), "Save")) return;
            if (!BeginHistoryOperation()) return;
            var generation = m_historyUiGeneration;
            try
            {
                var result = await RunHistoryOperation(HistoryTarget(), GatewayTestHistoryManager.Operation.Configure, m_historySnapshot, size, records, true, !m_historyByteLimitDirty);
                if (this == null || generation != m_historyUiGeneration) return;
                if (!result.Success && !result.ConfigurationSaved) { ShowHistoryFailure(result); return; }
                m_historySettingsDirty = false;
                m_historyByteLimitDirty = false;
                RenderTestHistory(result.data);
                m_historyStatus.text = McpWindowLocalization.Text("Storage limits saved. Restart Gateway to apply; history was not deleted.");
                if (!result.Success) m_historyStatus.text += " " + McpWindowLocalization.Text("Usage scan unavailable. Refresh History to read usage.");
            }
            catch { if (this != null && generation == m_historyUiGeneration) ShowHistoryFailure(null); }
            finally { EndHistoryOperation(generation); }
        }
    }
}
