using NUnit.Framework;
using SGG.UnityMCP.Editor.Setup;
using SGG.UnityMCP.Editor.UI;
using SGG.UnityMCP.Editor.UI.State;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class Harden09A2ViewModelTests
    {
        const string Private = "PRIVATE_STATUS_SENTINEL_9A2";

        [Test]
        public void RuntimePolicySummaryCoversUnavailableMalformedOlderActiveAndDegradedStates()
        {
            Assert.That(McpRuntimePolicyViewModel.BuildSummary(null), Is.EqualTo("Unavailable until Refresh Health succeeds."));

            var status = Status();
            Assert.That(McpRuntimePolicyViewModel.BuildSummary(status), Is.EqualTo("Unavailable until Refresh Health succeeds."));
            status.HealthProbed = true;
            status.HealthOk = true;
            status.PolicyMetadataMalformed = true;
            Assert.That(McpRuntimePolicyViewModel.BuildSummary(status), Is.EqualTo("Unavailable: malformed policy metadata was rejected safely. Check the running Gateway version, then Refresh Health."));
            status.PolicyMetadataMalformed = false;
            Assert.That(McpRuntimePolicyViewModel.BuildSummary(status), Is.EqualTo("Unavailable from this older Gateway health contract."));

            status.PolicyMetadataAvailable = true;
            status.PolicyConfigured = true;
            status.PolicyState = "active";
            status.PolicyRevision = "public-revision";
            status.PolicyLoadedAt = "public-loaded";
            status.PolicyLastAttemptAt = "public-attempt";
            var active = McpRuntimePolicyViewModel.BuildSummary(status);
            Assert.That(active, Is.EqualTo("state=active, configured=yes, degraded=no, revision=public-revision, loaded=public-loaded, attempt=public-attempt"));

            status.PolicyState = "degraded";
            status.PolicyDegraded = true;
            status.PolicyLastErrorCode = "policy_invalid";
            status.PolicyLastErrorKind = "schema";
            status.PolicyDegradedSince = "public-since";
            var degraded = McpRuntimePolicyViewModel.BuildSummary(status);
            Assert.That(degraded, Does.Contain("error=policy_invalid/schema, degraded-since=public-since"));
            Assert.That(degraded, Does.EndWith("Risky commands fail closed; permitted reads use last-known-good policy. Restore a valid policy, then Refresh Health. No blocked command is replayed automatically. Do not restart for a policy-only recovery."));

            Assert.That(active, Does.Not.Contain(Private));
            Assert.That(degraded, Does.Not.Contain(Private));
        }

        [Test]
        public void TimingSummaryDefaultsBoundsNormalizesAndIsDeterministic()
        {
            Assert.That(McpWindowTimingViewModel.LatestSummary(null), Is.EqualTo("No MCP Window timing captured yet."));
            Assert.That(McpWindowTimingViewModel.LatestSummary(string.Empty), Is.EqualTo("No MCP Window timing captured yet."));
            Assert.That(McpWindowTimingViewModel.BuildSummary("RefreshAll", -4, null), Is.EqualTo("RefreshAll total=0ms; no measured substeps"));

            var details = new string('d', McpWindowTimingViewModel.MaxDetailChars + 20);
            var first = McpWindowTimingViewModel.BuildSummary("RefreshAll", 17, details);
            var second = McpWindowTimingViewModel.BuildSummary("RefreshAll", 17, details);
            Assert.That(first, Is.EqualTo(second));
            Assert.That(first, Does.EndWith(new string('d', McpWindowTimingViewModel.MaxDetailChars) + "..."));
            Assert.That(first.Length, Is.LessThanOrEqualTo(McpWindowTimingViewModel.MaxSourceChars + McpWindowTimingViewModel.MaxDetailChars + 32));
        }

        [Test]
        public void DiagnosticsBuildPreservesExactHeadingAndFieldOrderWithNullSafety()
        {
            const string expected = "SGG Unity MCP Diagnostics\n"
                + "Runtime: \nGateway config: \nPolicy file: \nGateway log: \nHeartbeat: \n"
                + "Problem reports folder: \nBridge token: \nMCP token: \nCanonicalPath dependency: \n"
                + "PathGuard: \nFeature flags: \nAudit log: \nMCP Window timing: \nLast errors: \n"
                + "Redaction applied: true";

            Assert.That(McpDiagnosticsCopyViewModel.Build(null), Is.EqualTo(expected));
            Assert.That(McpDiagnosticsCopyViewModel.Build(EmptyDiagnostics()), Is.EqualTo(expected));
        }

        [Test]
        public void DiagnosticsRedactionRemovesPrefixedBearerCookieJsonAndTokenValues()
        {
            const string input = "INFO Authorization: Bearer AUTH_9A2 request=kept\n"
                + "Cookie: session=COOKIE_9A2; refresh=COOKIE_REFRESH_9A2\n"
                + "access_token=Bearer ACCESS_9A2 benign=visible\n"
                + "{\"authorization\":\"Bearer JSON_AUTH_9A2\",\"refresh_token\":\"JSON_TOKEN_9A2\",\"status\":\"healthy\"}";

            var result = McpDiagnosticsCopyViewModel.Redact(input);

            Assert.That(result, Does.Not.Contain("AUTH_9A2"));
            Assert.That(result, Does.Not.Contain("COOKIE_9A2"));
            Assert.That(result, Does.Not.Contain("COOKIE_REFRESH_9A2"));
            Assert.That(result, Does.Not.Contain("ACCESS_9A2"));
            Assert.That(result, Does.Not.Contain("JSON_TOKEN_9A2"));
            Assert.That(result, Does.Contain("request=kept"));
            Assert.That(result, Does.Contain("benign=visible"));
            Assert.That(result, Does.Contain("\"status\":\"healthy\""));
            Assert.That(McpDiagnosticsCopyViewModel.Redact(null), Is.Empty);
        }

        [Test]
        public void DiagnosticsBuildBoundsOutputAfterRedaction()
        {
            var large = new string('x', 100000);
            var input = new McpDiagnosticsCopyViewModel(large, large, large, large, large, large, large, large, large, large, large, large, large, "token=BOUND_SECRET_9A2\n" + large);

            var result = McpDiagnosticsCopyViewModel.Build(input);

            Assert.That(result.Length, Is.LessThanOrEqualTo(McpDiagnosticsCopyViewModel.MaxOutputChars));
            Assert.That(result, Does.Not.Contain("BOUND_SECRET_9A2"));
            Assert.That(result, Does.Contain("Runtime: "));
            Assert.That(result, Does.Contain("Problem reports folder: "));
            Assert.That(result, Does.Contain("MCP Window timing: "));
            Assert.That(result, Does.Contain("Last errors: " + McpDiagnosticsCopyViewModel.TruncationMarker));
            Assert.That(result, Does.Contain(McpDiagnosticsCopyViewModel.TruncationMarker));
            Assert.That(result, Does.EndWith("Redaction applied: true"));
        }

        [Test]
        public void DiagnosticsBuildRedactsLongValidJsonSecretBeforeHeadBounding()
        {
            const string startSentinel = "CROSS_BOUNDARY_SECRET_START_9A2";
            const string endSentinel = "CROSS_BOUNDARY_SECRET_END_9A2";
            var secret = startSentinel + new string('q', McpDiagnosticsCopyViewModel.MaxFieldChars + 100) + endSentinel;
            var runtime = "{\"refresh_token\":\"" + secret + "\",\"status\":\"healthy\"}";
            var input = new McpDiagnosticsCopyViewModel(runtime, null, null, null, null, null, null, null, null, null, null, null, null, null);

            var result = McpDiagnosticsCopyViewModel.Build(input);

            Assert.That(result, Does.Contain("\"refresh_token\":\"[REDACTED]\""));
            Assert.That(result, Does.Not.Contain(startSentinel));
            Assert.That(result, Does.Not.Contain(endSentinel));
            Assert.That(result, Does.Not.Contain(new string('q', 32)));
            Assert.That(result, Does.Not.Contain(secret));
            Assert.That(result, Does.EndWith("Redaction applied: true"));
        }

        [Test]
        public void DiagnosticsRedactionFailsClosedForUnterminatedJsonSecretsAndPreservesBenignMalformedText()
        {
            const string benign = "WARN {\"status\":\"unfinished benign text";
            const string input = "ERROR {\"refresh_token\":\"REFRESH_UNTERMINATED_9A2\r\n"
                + "ERROR {\"private_api_key\":\"API_UNTERMINATED_9A2\n"
                + "ERROR {\"db_password\":\"PASSWORD_UNTERMINATED_9A2\n"
                + "ERROR {\"client_secret\":\"SECRET_UNTERMINATED_9A2\n"
                + "ERROR {\"session_cookie\":\"COOKIE_UNTERMINATED_9A2\n"
                + "ERROR {\"proxy_authorization\":\"Bearer AUTH_UNTERMINATED_9A2\n"
                + benign;

            var result = McpDiagnosticsCopyViewModel.Redact(input);

            Assert.That(result, Does.Not.Contain("UNTERMINATED_9A2"));
            Assert.That(result, Does.Contain("\"refresh_token\":\"[REDACTED]"));
            Assert.That(result, Does.Contain("\"proxy_authorization\":\"[REDACTED]"));
            Assert.That(result, Does.EndWith(benign));
        }

        [Test]
        public void DiagnosticsRedactionUsesSeparatorDelimitedCredentialComponents()
        {
            const string input = "token=RAW_CREDENTIAL_9A2\n"
                + "access_token=RAW_CREDENTIAL_9A2\n"
                + "refresh-token=RAW_CREDENTIAL_9A2\n"
                + "token_value=RAW_CREDENTIAL_9A2\n"
                + "api_key=RAW_CREDENTIAL_9A2\n"
                + "api-key=RAW_CREDENTIAL_9A2\n"
                + "apikey=RAW_CREDENTIAL_9A2\n"
                + "private_api_key=RAW_CREDENTIAL_9A2\n"
                + "password=RAW_CREDENTIAL_9A2\n"
                + "db_password=RAW_CREDENTIAL_9A2\n"
                + "secret=RAW_CREDENTIAL_9A2\n"
                + "client_secret=RAW_CREDENTIAL_9A2\n"
                + "session_cookie=session=RAW_CREDENTIAL_9A2; refresh=RAW_COOKIE_REFRESH_9A2\n"
                + "proxy_authorization=Bearer RAW_CREDENTIAL_9A2 benign=preserved";

            var result = McpDiagnosticsCopyViewModel.Redact(input);

            Assert.That(result, Does.Not.Contain("RAW_CREDENTIAL_9A2"));
            Assert.That(result, Does.Not.Contain("RAW_COOKIE_REFRESH_9A2"));
            Assert.That(result, Does.Contain("proxy_authorization=[REDACTED] benign=preserved"));
            Assert.That(result, Does.Contain("session_cookie=[REDACTED]"));
        }

        [Test]
        public void DiagnosticsRedactionPreservesCredentialSubstringsWithoutSeparators()
        {
            const string input = "secretary=Alice tokenizer=BPE secretariat=office cookiecutter=tool authorizationStatus=ready passwordless=true\n"
                + "{'secretary':'Alice','tokenizer':'BPE'}";

            Assert.That(McpDiagnosticsCopyViewModel.Redact(input), Is.EqualTo(input));
        }

        [Test]
        public void DiagnosticsRedactionFailsClosedForSingleQuotedJsonLikeSecrets()
        {
            const string benign = "{'secretary':'Alice','tokenizer':'BPE'}";
            const string input = "{'refresh_token':'SINGLE_CLOSED_SECRET_9A2'}\n"
                + "{'client_secret':'SINGLE_UNTERMINATED_SECRET_9A2\n"
                + benign;

            var result = McpDiagnosticsCopyViewModel.Redact(input);

            Assert.That(result, Does.Not.Contain("SINGLE_CLOSED_SECRET_9A2"));
            Assert.That(result, Does.Not.Contain("SINGLE_UNTERMINATED_SECRET_9A2"));
            Assert.That(result, Does.Contain("{'refresh_token':'[REDACTED]'}"));
            Assert.That(result, Does.Contain("{'client_secret':'[REDACTED]"));
            Assert.That(result, Does.EndWith(benign));
        }

        [Test]
        public void DiagnosticsRedactionAcceptsMismatchedKeyQuoteDelimiters()
        {
            const string benign = "{\"secretary':'Alice'}";
            const string input = "{\"refresh_token':'MIXED_DOUBLE_SINGLE_CLOSED_9A2'}\n"
                + "{'refresh_token\":\"MIXED_SINGLE_DOUBLE_CLOSED_9A2\"}\n"
                + "{\"client_secret':'MIXED_DOUBLE_SINGLE_OPEN_9A2\n"
                + "{'client_secret\":\"MIXED_SINGLE_DOUBLE_OPEN_9A2\n"
                + benign;

            var result = McpDiagnosticsCopyViewModel.Redact(input);

            Assert.That(result, Does.Not.Contain("MIXED_DOUBLE_SINGLE_CLOSED_9A2"));
            Assert.That(result, Does.Not.Contain("MIXED_SINGLE_DOUBLE_CLOSED_9A2"));
            Assert.That(result, Does.Not.Contain("MIXED_DOUBLE_SINGLE_OPEN_9A2"));
            Assert.That(result, Does.Not.Contain("MIXED_SINGLE_DOUBLE_OPEN_9A2"));
            Assert.That(result, Does.Contain("{\"refresh_token':'[REDACTED]'}"));
            Assert.That(result, Does.Contain("{'refresh_token\":\"[REDACTED]\"}"));
            Assert.That(result, Does.EndWith(benign));
        }

        [Test]
        public void ProductionLastErrorLinesRedactsMalformedNewestErrorAndTailBoundKeepsNewest()
        {
            const string benign = "ERROR benign {'secretary':'Alice','tokenizer':'BPE'}";
            const string newest = "ERROR newest failure id=NEWEST_ERROR_9A2 {'client_secret':'NEWEST_SECRET_9A2";
            var logTail = "ERROR older failure " + new string('o', McpDiagnosticsCopyViewModel.MaxFieldChars + 1000) + "\n" + benign + "\n" + newest;

            var selected = UnityMcpSetupWindow.LastErrorLines(logTail);
            var input = new McpDiagnosticsCopyViewModel(null, null, null, null, null, null, null, null, null, null, null, null, null, selected);
            var result = McpDiagnosticsCopyViewModel.Build(input);

            Assert.That(selected, Does.Not.Contain("NEWEST_SECRET_9A2"));
            Assert.That(selected, Does.Contain("{'secretary':'Alice','tokenizer':'BPE'}"));
            Assert.That(result, Does.Contain("Last errors: " + McpDiagnosticsCopyViewModel.TruncationMarker));
            Assert.That(result, Does.Contain("NEWEST_ERROR_9A2"));
            Assert.That(result, Does.Not.Contain("NEWEST_SECRET_9A2"));
            Assert.That(result, Does.Contain("{'secretary':'Alice','tokenizer':'BPE'}"));
            Assert.That(result, Does.EndWith("Redaction applied: true"));
            Assert.That(result.Length, Is.LessThanOrEqualTo(McpDiagnosticsCopyViewModel.MaxOutputChars));
        }

        [Test]
        public void ProductionLastErrorLinesAndBuildRedactMismatchedKeyQuotes()
        {
            const string benign = "ERROR benign {\"secretary':'Alice'}";
            const string newest = "ERROR newest {\"refresh_token':'LAST_ERROR_MIXED_SECRET_9A2";
            var selected = UnityMcpSetupWindow.LastErrorLines(benign + "\n" + newest);
            var input = new McpDiagnosticsCopyViewModel(null, null, null, null, null, null, null, null, null, null, null, null, null, selected);

            var result = McpDiagnosticsCopyViewModel.Build(input);

            Assert.That(selected, Does.Not.Contain("LAST_ERROR_MIXED_SECRET_9A2"));
            Assert.That(selected, Does.Contain("{\"secretary':'Alice'}"));
            Assert.That(result, Does.Not.Contain("LAST_ERROR_MIXED_SECRET_9A2"));
            Assert.That(result, Does.Contain("{\"secretary':'Alice'}"));
            Assert.That(result, Does.EndWith("Redaction applied: true"));
        }

        static GatewayProcessManager.GatewayRuntimeStatus Status()
        {
            return new GatewayProcessManager.GatewayRuntimeStatus
            {
                HealthBody = Private,
                LogPath = Private,
                LogTail = Private,
            };
        }

        static McpDiagnosticsCopyViewModel EmptyDiagnostics()
        {
            return new McpDiagnosticsCopyViewModel(null, null, null, null, null, null, null, null, null, null, null, null, null, null);
        }
    }
}
