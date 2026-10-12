using System.Text.RegularExpressions;

namespace SGG.UnityMCP.Editor.UI.State
{
    internal sealed class McpDiagnosticsCopyViewModel
    {
        const string ComponentPrefix = "(?:[a-z0-9]+[_-])*";
        const string ComponentSuffix = "(?:[_-][a-z0-9]+)*";
        const string CredentialKeyPattern = ComponentPrefix + "(?:token|api[_-]?key|password|secret)" + ComponentSuffix;
        const string AuthorizationKeyPattern = ComponentPrefix + "authorization" + ComponentSuffix;
        const string CookieKeyPattern = ComponentPrefix + "cookie" + ComponentSuffix;
        const string SecretKeyPattern = "(?:" + CredentialKeyPattern + "|" + AuthorizationKeyPattern + "|" + CookieKeyPattern + ")";

        // Clipboard diagnostics remain useful while preventing unexpectedly large UI values from producing unbounded output.
        public const int MaxFieldChars = 4096;
        public const int MaxOutputChars = 65536;
        public const string TruncationMarker = "[... truncated ...]";

        public McpDiagnosticsCopyViewModel(
            string runtime,
            string gatewayConfig,
            string policyFile,
            string gatewayLog,
            string heartbeat,
            string problemReportsFolder,
            string bridgeToken,
            string mcpToken,
            string canonicalPathDependency,
            string pathGuard,
            string featureFlags,
            string auditLog,
            string windowTiming,
            string lastErrors)
        {
            Runtime = runtime;
            GatewayConfig = gatewayConfig;
            PolicyFile = policyFile;
            GatewayLog = gatewayLog;
            Heartbeat = heartbeat;
            ProblemReportsFolder = problemReportsFolder;
            BridgeToken = bridgeToken;
            McpToken = mcpToken;
            CanonicalPathDependency = canonicalPathDependency;
            PathGuard = pathGuard;
            FeatureFlags = featureFlags;
            AuditLog = auditLog;
            WindowTiming = windowTiming;
            LastErrors = lastErrors;
        }

        public string Runtime { get; private set; }
        public string GatewayConfig { get; private set; }
        public string PolicyFile { get; private set; }
        public string GatewayLog { get; private set; }
        public string Heartbeat { get; private set; }
        public string ProblemReportsFolder { get; private set; }
        public string BridgeToken { get; private set; }
        public string McpToken { get; private set; }
        public string CanonicalPathDependency { get; private set; }
        public string PathGuard { get; private set; }
        public string FeatureFlags { get; private set; }
        public string AuditLog { get; private set; }
        public string WindowTiming { get; private set; }
        public string LastErrors { get; private set; }

        public static string Build(McpDiagnosticsCopyViewModel input)
        {
            if (input == null)
            {
                input = new McpDiagnosticsCopyViewModel(null, null, null, null, null, null, null, null, null, null, null, null, null, null);
            }

            var diagnostics = "SGG Unity MCP Diagnostics\n"
                + "Runtime: " + HeadField(input.Runtime) + "\n"
                + "Gateway config: " + HeadField(input.GatewayConfig) + "\n"
                + "Policy file: " + HeadField(input.PolicyFile) + "\n"
                + "Gateway log: " + HeadField(input.GatewayLog) + "\n"
                + "Heartbeat: " + HeadField(input.Heartbeat) + "\n"
                + "Problem reports folder: " + HeadField(input.ProblemReportsFolder) + "\n"
                + "Bridge token: " + HeadField(input.BridgeToken) + "\n"
                + "MCP token: " + HeadField(input.McpToken) + "\n"
                + "CanonicalPath dependency: " + HeadField(input.CanonicalPathDependency) + "\n"
                + "PathGuard: " + HeadField(input.PathGuard) + "\n"
                + "Feature flags: " + HeadField(input.FeatureFlags) + "\n"
                + "Audit log: " + HeadField(input.AuditLog) + "\n"
                + "MCP Window timing: " + HeadField(input.WindowTiming) + "\n"
                + "Last errors: " + TailField(input.LastErrors) + "\n"
                + "Redaction applied: true";
            return diagnostics.Length <= MaxOutputChars ? diagnostics : EmptyDiagnosticsText();
        }

        public static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var redacted = Regex.Replace(text, "(?i)((?:\\\"|')" + SecretKeyPattern + "(?:\\\"|')[ \\t\\r\\n]*:[ \\t\\r\\n]*)(\\\"|')(?:\\\\.|(?!\\2)[^\\\\\\r\\n])*\\2", "$1$2[REDACTED]$2");
            redacted = Regex.Replace(redacted, "(?i)((?:\\\"|')" + SecretKeyPattern + "(?:\\\"|')[ \\t]*:[ \\t]*)(\\\"|')(?:\\\\.|(?!\\2)[^\\\\\\r\\n])*(?=\\r?\\n|$)", "$1$2[REDACTED]");
            redacted = Regex.Replace(redacted, "(?i)((?:\\\"|')" + SecretKeyPattern + "(?:\\\"|')[ \\t]*:[ \\t]*)(?!\\\"|')[^,}\\r\\n]+", "$1[REDACTED]");
            redacted = Regex.Replace(redacted, "(?i)(\\b" + AuthorizationKeyPattern + "[ \\t]*[:=][ \\t]*)(?:bearer[ \\t]+)?(?:\\\"(?:\\\\.|[^\\\"\\\\])*\\\"|'[^'\\r\\n]*'|[^\\s,;}]+)", "$1[REDACTED]");
            redacted = Regex.Replace(redacted, "(?im)(\\b" + CookieKeyPattern + "[ \\t]*[:=][ \\t]*)[^\\r\\n]+", "$1[REDACTED]");
            redacted = Regex.Replace(redacted, "(?i)(\\b" + CredentialKeyPattern + "[ \\t]*[:=][ \\t]*)(?:bearer[ \\t]+[^\\s,;}]+|\\\"(?:\\\\.|[^\\\"\\\\])*\\\"|'[^'\\r\\n]*'|[^\\s,;}]+)", "$1[REDACTED]");
            return redacted;
        }

        static string HeadField(string value)
        {
            var safe = Redact(value);
            return safe.Length <= MaxFieldChars
                ? safe
                : safe.Substring(0, MaxFieldChars - TruncationMarker.Length) + TruncationMarker;
        }

        static string TailField(string value)
        {
            var safe = Redact(value);
            return safe.Length <= MaxFieldChars
                ? safe
                : TruncationMarker + safe.Substring(safe.Length - (MaxFieldChars - TruncationMarker.Length));
        }

        static string EmptyDiagnosticsText()
        {
            return "SGG Unity MCP Diagnostics\n"
                + "Runtime: \nGateway config: \nPolicy file: \nGateway log: \nHeartbeat: \n"
                + "Problem reports folder: \nBridge token: \nMCP token: \nCanonicalPath dependency: \n"
                + "PathGuard: \nFeature flags: \nAudit log: \nMCP Window timing: \nLast errors: \n"
                + "Redaction applied: true";
        }
    }
}
