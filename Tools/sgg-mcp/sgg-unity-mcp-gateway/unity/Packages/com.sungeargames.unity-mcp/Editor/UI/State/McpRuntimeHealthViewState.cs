using System;
using SGG.UnityMCP.Editor.Setup;

namespace SGG.UnityMCP.Editor.UI.State
{
    internal sealed class McpRuntimeHealthViewState
    {
        McpRuntimeHealthViewState(GatewayProcessManager.GatewayRuntimeStatus processStatus, GatewayInstaller.GatewayInstallStatus installStatus, bool localHeartbeatStale)
        {
            VersionMismatch = processStatus != null && installStatus != null
                && !string.IsNullOrEmpty(processStatus.HealthVersion)
                && !string.IsNullOrEmpty(installStatus.InstalledRuntimeVersion)
                && !string.Equals(processStatus.HealthVersion, installStatus.InstalledRuntimeVersion, StringComparison.Ordinal);

            var hasLiveProjectCoverage = processStatus != null
                && (processStatus.LiveProjectsCount > 0 || processStatus.LiveProjectRecordsCount > 0);
            ActionableStaleProjects = processStatus != null
                && processStatus.StaleProjectRecordsCount > 0
                && !hasLiveProjectCoverage;

            ProcessNeedsRestart = processStatus != null && installStatus != null
                && processStatus.HealthOk
                && VersionMismatch;
            StartOrRestartActionNeeded = processStatus != null && installStatus != null
                && installStatus.CanStart
                && (ProcessNeedsRestart || (processStatus.HealthProbed && !processStatus.HealthOk));
            HealthRefreshActionNeeded = processStatus == null
                || !processStatus.HealthProbed
                || !processStatus.HealthOk
                || processStatus.PolicyDegraded
                || processStatus.PolicyMetadataMalformed
                || ActionableStaleProjects
                || localHeartbeatStale;
            CurrentHealthyRuntime = processStatus != null
                && processStatus.HealthOk
                && !VersionMismatch
                && !processStatus.PolicyDegraded
                && !processStatus.PolicyMetadataMalformed
                && !ActionableStaleProjects;
        }

        public bool VersionMismatch { get; private set; }
        public bool ActionableStaleProjects { get; private set; }
        public bool ProcessNeedsRestart { get; private set; }
        public bool StartOrRestartActionNeeded { get; private set; }
        public bool HealthRefreshActionNeeded { get; private set; }
        public bool CurrentHealthyRuntime { get; private set; }

        public static McpRuntimeHealthViewState Evaluate(GatewayProcessManager.GatewayRuntimeStatus processStatus, GatewayInstaller.GatewayInstallStatus installStatus, bool localHeartbeatStale = false)
        {
            return new McpRuntimeHealthViewState(processStatus, installStatus, localHeartbeatStale);
        }
    }
}
