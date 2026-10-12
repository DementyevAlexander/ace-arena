using SGG.UnityMCP.Editor.Setup;

namespace SGG.UnityMCP.Editor.UI.State
{
    internal static class McpRuntimePolicyViewModel
    {
        public static string BuildSummary(GatewayProcessManager.GatewayRuntimeStatus status)
        {
            if (status == null || !status.HealthProbed || !status.HealthOk)
            {
                return "Unavailable until Refresh Health succeeds.";
            }

            if (status.PolicyMetadataMalformed)
            {
                return "Unavailable: malformed policy metadata was rejected safely. Check the running Gateway version, then Refresh Health.";
            }

            if (!status.PolicyMetadataAvailable)
            {
                return "Unavailable from this older Gateway health contract.";
            }

            var summary = "state=" + status.PolicyState + ", configured=" + (status.PolicyConfigured ? "yes" : "no") + ", degraded=" + (status.PolicyDegraded ? "yes" : "no");
            if (!string.IsNullOrEmpty(status.PolicyRevision)) summary += ", revision=" + status.PolicyRevision;
            if (!string.IsNullOrEmpty(status.PolicyLoadedAt)) summary += ", loaded=" + status.PolicyLoadedAt;
            if (!string.IsNullOrEmpty(status.PolicyLastAttemptAt)) summary += ", attempt=" + status.PolicyLastAttemptAt;
            if (!string.IsNullOrEmpty(status.PolicyLastErrorCode)) summary += ", error=" + status.PolicyLastErrorCode + "/" + status.PolicyLastErrorKind;
            if (!string.IsNullOrEmpty(status.PolicyDegradedSince)) summary += ", degraded-since=" + status.PolicyDegradedSince;
            return status.PolicyDegraded
                ? summary + ". Risky commands fail closed; permitted reads use last-known-good policy. Restore a valid policy, then Refresh Health. No blocked command is replayed automatically. Do not restart for a policy-only recovery."
                : summary;
        }
    }
}
