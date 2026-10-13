using System.Text;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Setup;
using SGG.UnityMCP.Editor.UI;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class GatewayHealthParserTests
    {
        [TestCase("  TCP    127.0.0.1:27182      0.0.0.0:0      LISTENING       68948", 68948)]
        [TestCase("  TCP    [::1]:27182          [::]:0         LISTENING       42", 42)]
        public void ListeningPidParserAcceptsOnlyTheExactLocalGatewayPort(string line, int expectedPid)
        {
            Assert.That(GatewayProcessManager.TryParseListeningPid(line, 27182, out var pid), Is.True);
            Assert.That(pid, Is.EqualTo(expectedPid));
        }

        [TestCase("  TCP    127.0.0.1:27183      0.0.0.0:0      LISTENING       68948")]
        [TestCase("  TCP    127.0.0.1:50000      127.0.0.1:27182 ESTABLISHED     68948")]
        [TestCase("  UDP    127.0.0.1:27182      *:*                            68948")]
        [TestCase("  TCP    127.0.0.1:27182      0.0.0.0:0      LISTENING       invalid")]
        public void ListeningPidParserRejectsUnrelatedOrMalformedNetstatLines(string line)
        {
            Assert.That(GatewayProcessManager.TryParseListeningPid(line, 27182, out _), Is.False);
        }

        [Test]
        public void ListeningPidInspectionAcceptsDuplicateAddressFamiliesForOneProcess()
        {
            var output = "TCP 127.0.0.1:27182 0.0.0.0:0 LISTENING 68948\nTCP [::1]:27182 [::]:0 LISTENING 68948";

            Assert.That(GatewayProcessManager.TryFindListeningPid(output, 27182, out var pid, out var message), Is.True, message);
            Assert.That(pid, Is.EqualTo(68948));
        }

        [TestCase("TCP 127.0.0.1:27182 0.0.0.0:0 LISTENING")]
        [TestCase("TCP 127.0.0.1:27182 0.0.0.0:0 LISTENING invalid\nTCP [::1]:27182 [::]:0 LISTENING 68948")]
        [TestCase("TCP 127.0.0.1:27182 0.0.0.0:0 LISTENING 68948\nTCP [::1]:27182 [::]:0 LISTENING 42")]
        public void ListeningPidInspectionFailsClosedForAmbiguousTargetLines(string output)
        {
            Assert.That(GatewayProcessManager.TryFindListeningPid(output, 27182, out _, out var message), Is.False);
            Assert.That(message, Is.Not.Empty);
        }

        [TestCase(false, false)]
        [TestCase(true, true)]
        public void ExplicitRestartAcceptsInspectedAbsenceWithoutStoppingAnyProcess(bool allowAbsent, bool expected)
        {
            var inspections = 0;
            var stops = 0;
            var result = GatewayProcessManager.StopInspectedGatewayListener(allowAbsent,
                (out int pid, out string message) => { inspections++; pid = 0; message = string.Empty; return true; },
                (int pid, out string message) => { stops++; message = "unexpected stop"; return true; }, out var outcome);
            Assert.That(result, Is.EqualTo(expected));
            Assert.That(inspections, Is.EqualTo(1));
            Assert.That(stops, Is.Zero);
            Assert.That(outcome, Does.Contain("No gateway listener"));
        }

        [TestCase(false, "inspection failed")]
        [TestCase(true, "inspection failed")]
        [TestCase(true, "truncated inventory")]
        [TestCase(true, "ambiguous listeners")]
        public void RestartNeverTreatsFailedInspectionAsAbsence(bool allowAbsent, string failure)
        {
            var stops = 0;
            var result = GatewayProcessManager.StopInspectedGatewayListener(allowAbsent,
                (out int pid, out string message) => { pid = 0; message = failure; return false; },
                (int pid, out string message) => { stops++; message = "unexpected stop"; return true; }, out var outcome);
            Assert.That(result, Is.False);
            Assert.That(outcome, Is.EqualTo(failure));
            Assert.That(stops, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RestartRejectsInvalidPidWithoutStoppingAnyProcess(bool allowAbsent)
        {
            var stops = 0;
            var result = GatewayProcessManager.StopInspectedGatewayListener(allowAbsent,
                (out int pid, out string message) => { pid = -1; message = string.Empty; return true; },
                (int pid, out string message) => { stops++; message = "unexpected stop"; return true; }, out var outcome);
            Assert.That(result, Is.False);
            Assert.That(outcome, Does.Contain("invalid PID"));
            Assert.That(stops, Is.Zero);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void RestartRequiresOneSuccessfulStopOfTheInspectedPid(bool allowAbsent, bool stopSuccess)
        {
            var stops = 0;
            var result = GatewayProcessManager.StopInspectedGatewayListener(allowAbsent,
                (out int pid, out string message) => { pid = 42; message = string.Empty; return true; },
                (int pid, out string message) => { Assert.That(pid, Is.EqualTo(42)); stops++; message = "stop outcome"; return stopSuccess; }, out var outcome);
            Assert.That(result, Is.EqualTo(stopSuccess));
            Assert.That(stops, Is.EqualTo(1));
            Assert.That(outcome, Is.EqualTo("stop outcome"));
        }

        const string Revision = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        [Test]
        public void TcpHealthRequestUsesFixedLoopbackAndBoundsTokenHeader()
        {
            Assert.That(GatewayProcessManager.TryBuildHealthTcpRequest("example-token", out var request, out var error), Is.True, error);
            var text = Encoding.ASCII.GetString(request);
            Assert.That(text, Does.StartWith("GET /health HTTP/1.0\r\nHost: 127.0.0.1:27182\r\nConnection: close\r\n"));
            Assert.That(text, Does.Contain("X-SGG-Unity-MCP-Token: example-token\r\n\r\n"));

            foreach (var invalid in new[] { "token\r\nX-Injected: 1", "token with spaces", "non-ascii-ä", new string('x', 4097) })
            {
                Assert.That(GatewayProcessManager.TryBuildHealthTcpRequest(invalid, out request, out error), Is.False);
                Assert.That(request, Is.Null);
                Assert.That(error, Is.Not.Empty);
            }
        }

        [Test]
        public void TcpHealthResponseParsesBoundedHttp10CloseFraming()
        {
            var payload = "{\"ok\":true,\"version\":\"2026.9.23-4\",\"label\":\"тест\"}";
            var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\ncontent-type: application/json\r\nConnection: close\r\n\r\n" + payload);

            Assert.That(GatewayProcessManager.TryParseHealthTcpResponse(response, out var body, out var error), Is.True, error);
            Assert.That(body, Is.EqualTo(payload));
        }

        [TestCase("HTTP/1.1 401 Unauthorized\r\n\r\n{\"error\":\"invalid_token\"}")]
        [TestCase("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n3\r\nabc\r\n0\r\n\r\n")]
        [TestCase("HTTP/1.1 200 OK\r\n\r\n{\"ok\":true}")]
        [TestCase("not http\r\n\r\n{\"ok\":true}")]
        public void TcpHealthResponseRejectsNonSuccessOrUnsupportedFraming(string response)
        {
            Assert.That(GatewayProcessManager.TryParseHealthTcpResponse(Encoding.ASCII.GetBytes(response), out var body, out var error), Is.False);
            Assert.That(body, Is.Empty);
            Assert.That(error, Is.Not.Empty);
        }

        [Test]
        public void TcpHealthResponseRejectsOversizedAndInvalidUtf8Payloads()
        {
            var oversizedHeaders = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nX-Long: " + new string('x', 8192) + "\r\n\r\n{}");
            Assert.That(GatewayProcessManager.TryParseHealthTcpResponse(oversizedHeaders, out var body, out var error), Is.False);
            Assert.That(error, Does.Contain("headers"));

            var oversized = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n\r\n" + new string('x', 65537));
            Assert.That(GatewayProcessManager.TryParseHealthTcpResponse(oversized, out body, out error), Is.False);
            Assert.That(body, Is.Empty);
            Assert.That(error, Does.Contain("bounded body limit"));

            var invalidUtf8 = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n\r\nabc");
            invalidUtf8[invalidUtf8.Length - 1] = 0xff;
            Assert.That(GatewayProcessManager.TryParseHealthTcpResponse(invalidUtf8, out body, out error), Is.False);
            Assert.That(body, Is.Empty);
            Assert.That(error, Does.Contain("UTF-8"));
        }

        [Test]
        public void ParsesNestedActivePolicyMetadata()
        {
            var status = GatewayProcessManager.ParseHealthMetadata(Health("{\"state\":\"active\",\"configured\":true,\"degraded\":false,\"effective_policy_revision\":\"" + Revision + "\",\"loaded_at\":\"2026-07-11T12:00:00.000Z\",\"last_attempt_at\":\"2026-07-11T12:01:00.000Z\",\"last_error_kind\":null,\"last_error_code\":null,\"degraded_since\":null}"));

            Assert.That(status.PolicyMetadataAvailable, Is.True);
            Assert.That(status.PolicyMetadataMalformed, Is.False);
            Assert.That(status.PolicyState, Is.EqualTo("active"));
            Assert.That(status.PolicyRevision, Is.EqualTo(Revision));
            Assert.That(status.LiveProjectsCount, Is.EqualTo(2));
        }

        [Test]
        public void ParsesNestedDegradedPolicyMetadata()
        {
            var status = GatewayProcessManager.ParseHealthMetadata(Health("{\"state\":\"degraded\",\"configured\":true,\"degraded\":true,\"effective_policy_revision\":\"" + Revision + "\",\"loaded_at\":\"2026-07-11T12:00:00.000Z\",\"last_attempt_at\":\"2026-07-11T12:02:00.000Z\",\"last_error_kind\":\"invalid_schema\",\"last_error_code\":\"policy_invalid\",\"degraded_since\":\"2026-07-11T12:02:00.000Z\"}"));

            Assert.That(status.PolicyMetadataAvailable, Is.True);
            Assert.That(status.PolicyDegraded, Is.True);
            Assert.That(status.PolicyLastErrorCode, Is.EqualTo("policy_invalid"));
            Assert.That(status.PolicyDegradedSince, Is.EqualTo("2026-07-11T12:02:00.000Z"));
        }

        [TestCase("{\"state\":\"active\",\"configured\":true,\"degraded\":false,\"effective_policy_revision\":\"sha256:BAD\",\"loaded_at\":\"2026-07-11T12:00:00.000Z\",\"last_attempt_at\":\"2026-07-11T12:01:00.000Z\"}")]
        [TestCase("{\"state\":\"degraded\",\"configured\":true,\"degraded\":true,\"effective_policy_revision\":null,\"loaded_at\":null,\"last_attempt_at\":\"not-a-time\",\"last_error_kind\":\"surprise\",\"last_error_code\":\"policy_invalid\",\"degraded_since\":null}")]
        public void RejectsMalformedPolicyMetadata(string policy)
        {
            var status = GatewayProcessManager.ParseHealthMetadata(Health(policy));

            Assert.That(status.PolicyMetadataAvailable, Is.False);
            Assert.That(status.PolicyMetadataMalformed, Is.True);
            Assert.That(status.PolicyRevision, Is.Empty);
        }

        [Test]
        public void AcceptsOlderHealthWithoutPolicyMetadata()
        {
            var status = GatewayProcessManager.ParseHealthMetadata("{\"version\":\"older\",\"live_projects_count\":1}");

            Assert.That(status.HealthVersion, Is.EqualTo("older"));
            Assert.That(status.PolicyMetadataAvailable, Is.False);
            Assert.That(status.PolicyMetadataMalformed, Is.False);
        }

        [Test]
        public void IgnoresPolicyLikeRootFieldsAndUsesNestedPolicyOnly()
        {
            var json = "{\"state\":\"degraded\",\"configured\":true,\"degraded\":true,\"effective_policy_revision\":\"sha256:BAD\",\"version\":\"current\",\"live_projects_count\":2,\"policy\":{\"state\":\"unconfigured\",\"configured\":false,\"degraded\":false,\"effective_policy_revision\":null,\"loaded_at\":null,\"last_attempt_at\":null,\"last_error_kind\":null,\"last_error_code\":null,\"degraded_since\":null}}";
            var status = GatewayProcessManager.ParseHealthMetadata(json);

            Assert.That(status.PolicyMetadataAvailable, Is.True);
            Assert.That(status.PolicyState, Is.EqualTo("unconfigured"));
            Assert.That(status.PolicyDegraded, Is.False);
        }

        [Test]
        public void ParsesBoundedHashedClientRootsWithoutRawPaths()
        {
            var roots = "{\"schema_version\":1,\"observations\":["
                + "{\"observation_id\":\"session-a\",\"status\":\"observed\",\"root_hashes\":[\"" + Revision + "\"],\"observed_at\":\"2026-07-26T00:00:00.000Z\"},"
                + "{\"observation_id\":\"session-b\",\"status\":\"unsupported\",\"root_hashes\":[],\"observed_at\":\"2026-07-26T00:00:00.000Z\"}]}";
            var status = GatewayProcessManager.ParseHealthMetadata(Health("null", roots));

            Assert.That(status.ClientRootsMetadataAvailable, Is.True);
            Assert.That(status.ClientRootsMetadataMalformed, Is.False);
            Assert.That(status.ClientRootsObservationCount, Is.EqualTo(2));
            Assert.That(status.ClientRootsObservedCount, Is.EqualTo(1));
            Assert.That(status.ClientRootsUnavailableCount, Is.EqualTo(1));
            Assert.That(status.ClientRootHashes, Is.EqualTo(new[] { Revision }));
        }

        [TestCase("{\"schema_version\":2,\"observations\":[]}")]
        [TestCase("{\"schema_version\":1,\"observations\":[{\"status\":\"observed\",\"root_hashes\":[\"C:/private/workspace\"]}]}")]
        [TestCase("{\"schema_version\":1,\"observations\":[{\"status\":\"surprise\",\"root_hashes\":[]}]}")]
        public void RejectsMalformedClientRootsMetadata(string roots)
        {
            var status = GatewayProcessManager.ParseHealthMetadata(Health("null", roots));

            Assert.That(status.ClientRootsMetadataAvailable, Is.False);
            Assert.That(status.ClientRootsMetadataMalformed, Is.True);
            Assert.That(status.ClientRootHashes, Is.Empty);
        }

        [Test]
        public void ReportsVerifiedAmbiguousAndMismatchedWorkspaceEvidence()
        {
            var network = new GatewayNetworkModeManager.NetworkStatus
            {
                OpenCodeTargetAvailable = true,
                OpenCodeWorkspaceHash = Revision,
            };
            var process = HealthyClientRoots(Revision);

            Assert.That(UnityMcpSetupWindow.ReportedWorkspaceVerification(network, process), Does.StartWith("Verified"));

            process.ClientRootHashes = new[] { Revision, "sha256:abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd" };
            Assert.That(UnityMcpSetupWindow.ReportedWorkspaceVerification(network, process), Does.StartWith("Ambiguous"));

            process.ClientRootHashes = new[] { "sha256:abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd" };
            Assert.That(UnityMcpSetupWindow.ReportedWorkspaceVerification(network, process), Does.StartWith("Mismatch"));
        }

        [Test]
        public void ReportsUnavailableWhenWorkspaceEvidenceCannotBeCompared()
        {
            var network = new GatewayNetworkModeManager.NetworkStatus
            {
                OpenCodeTargetAvailable = true,
                OpenCodeWorkspaceHash = Revision,
            };
            var process = HealthyClientRoots();
            process.ClientRootsObservationCount = 0;
            Assert.That(UnityMcpSetupWindow.ReportedWorkspaceVerification(network, process), Does.StartWith("Unavailable"));

            process.ClientRootsObservationCount = 1;
            process.ClientRootsMetadataMalformed = true;
            Assert.That(UnityMcpSetupWindow.ReportedWorkspaceVerification(network, process), Does.Contain("malformed"));

            process.ClientRootsMetadataMalformed = false;
            network.OpenCodeTargetAvailable = false;
            Assert.That(UnityMcpSetupWindow.ReportedWorkspaceVerification(network, process), Does.Contain("valid OpenCode workspace"));

            Assert.That(UnityMcpSetupWindow.ReportedWorkspaceVerification(network, null), Does.Contain("refresh Gateway health"));
        }

        static string Health(string policy)
        {
            return "{\"version\":\"2026.7.11-9\",\"live_projects_count\":2,\"stale_projects_count\":1,\"live_project_records_count\":2,\"stale_project_records_count\":1,\"invalid_project_records_count\":0,\"policy\":" + policy + "}";
        }

        static string Health(string policy, string clientRoots)
        {
            return "{\"version\":\"2026.7.26-1\",\"live_projects_count\":2,\"policy\":" + policy + ",\"client_roots\":" + clientRoots + "}";
        }

        static GatewayProcessManager.GatewayRuntimeStatus HealthyClientRoots(params string[] hashes)
        {
            return new GatewayProcessManager.GatewayRuntimeStatus
            {
                HealthProbed = true,
                HealthOk = true,
                ClientRootsMetadataAvailable = true,
                ClientRootsObservationCount = 1,
                ClientRootHashes = hashes,
            };
        }
    }
}
