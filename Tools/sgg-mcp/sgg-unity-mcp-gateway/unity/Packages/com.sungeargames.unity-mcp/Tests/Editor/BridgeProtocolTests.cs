using System.Linq;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Bridge;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class BridgeProtocolTests
    {
        const string Valid = "{\"schema_version\":1,\"request_id\":\"req-1\",\"project\":\"game\",\"command\":\"compile.status\",\"args\":{},\"risk\":\"read\",\"deadline_unix_ms\":1}";

        [Test]
        public void ParsesValidV1AndToleratesUnknownFields()
        {
            Assert.That(BridgeProtocol.TryParseInvoke(Valid.Substring(0, Valid.Length - 1) + ",\"future\":true}", out var request, out var error), Is.True, error);
            Assert.That(request.RequestId, Is.EqualTo("req-1"));
            Assert.That(request.Project, Is.EqualTo("game"));
            Assert.That(request.Command, Is.EqualTo("compile.status"));
            Assert.That(request.ArgsJson, Is.EqualTo("{}"));
        }

        [Test]
        public void ParsesCompleteCatalogBindingAndRejectsPartialBinding()
        {
            var bound = Valid.Substring(0, Valid.Length - 1)
                + ",\"expected_instance_id\":\"game-a\",\"expected_catalog_generation\":7,\"expected_commands_hash\":\"catalog-a\"}";
            Assert.That(BridgeProtocol.TryParseInvoke(bound, out var request, out var error), Is.True, error);
            Assert.That(request.HasCatalogBinding, Is.True);
            Assert.That(request.ExpectedInstanceId, Is.EqualTo("game-a"));
            Assert.That(request.ExpectedCatalogGeneration, Is.EqualTo(7));
            Assert.That(request.ExpectedCommandsHash, Is.EqualTo("catalog-a"));

            AssertInvalid(Valid.Substring(0, Valid.Length - 1) + ",\"expected_instance_id\":\"game-a\"}");
            AssertInvalid(Valid.Substring(0, Valid.Length - 1) + ",\"expected_instance_id\":\"game-a\",\"expected_catalog_generation\":0,\"expected_commands_hash\":\"catalog-a\"}");
        }

        [Test]
        public void PreservesHistoricalDirectValidationArgumentsForCompatibility()
        {
            const string json = "{\"schema_version\":1,\"request_id\":\"validation-guarded\",\"project\":\"game\",\"command\":\"validation.after_edit\",\"args\":{\"scope\":\"scripts\",\"compile\":\"request\",\"wait_for_ready\":true},\"risk\":\"write\",\"deadline_unix_ms\":1}";

            Assert.That(BridgeProtocol.TryParseInvoke(json, out var request, out var error), Is.True, error);
            Assert.That(request.Command, Is.EqualTo("validation.after_edit"));
            Assert.That(request.ArgsJson, Does.Contain("\"scope\":\"scripts\""));
            Assert.That(request.ArgsJson, Does.Contain("\"compile\":\"request\""));
            Assert.That(request.ArgsJson, Does.Contain("\"wait_for_ready\":true"));
        }

        [TestCase("{\"project\":\"game\",\"command\":\"compile.status\",\"args\":{}}", "protocol_version_mismatch")]
        [TestCase("{\"schema_version\":2,\"project\":\"game\",\"command\":\"compile.status\",\"args\":{}}", "protocol_version_mismatch")]
        [TestCase("{\"schema_version\":1,\"command\":\"compile.status\",\"args\":{}}", "protocol_validation_error")]
        [TestCase("{\"schema_version\":1,\"project\":\"game\",\"command\":\"compile.status\"}", "protocol_validation_error")]
        [TestCase("{\"schema_version\":1,\"project\":\"game\",\"command\":\"compile.status\",\"args\":[]}", "protocol_validation_error")]
        [TestCase("{\"schema_version\":1,\"request_id\":\"unsafe id\",\"project\":\"game\",\"command\":\"compile.status\",\"args\":{}}", "protocol_validation_error")]
        [TestCase("{\"schema_version\":1,\"project\":\"game\",\"command\":\"compile.status\",\"args\":{},\"risk\":\"unknown\"}", "protocol_validation_error")]
        [TestCase("{\"schema_version\":1,\"project\":\"game\",\"command\":\"compile.status\",\"args\":{},\"deadline_unix_ms\":0}", "protocol_validation_error")]
        [TestCase("not-json", "protocol_validation_error")]
        public void RejectsMalformedOrInvalidRequests(string json, string expectedCode)
        {
            Assert.That(BridgeProtocol.TryParseInvoke(json, out _, out var error), Is.False);
            Assert.That(error, Is.EqualTo(expectedCode));
        }

        [Test]
        public void BoundsIdsSettingsAndJson()
        {
            AssertInvalid(Valid.Replace("req-1", new string('x', 129)));
            AssertInvalid(Valid.Replace("compile.status", new string('x', 257)));
            AssertInvalid(Settings("{\"asset_index_cache_dir\":\"" + new string('x', 4097) + "\"}"));
            AssertInvalid(Settings("{\"feature_flags\":{" + string.Join(",", Enumerable.Range(0, 257).Select(index => "\"f" + index + "\":true")) + "}}"));
            AssertInvalid(Settings("{\"mono_behaviour_allowlist\":[" + string.Join(",", Enumerable.Repeat("{\"assembly_name\":\"Game\",\"full_type_name\":\"Game.Player\"}", 1001)) + "]}"));
        }

        [Test]
        public void BuildsCompleteBodyFreeProtocolErrorEnvelope()
        {
            var json = BridgeProtocol.ErrorResponse("protocol_validation_error", "Invalid bridge invoke request", "req-1", 12);
            Assert.That(json, Does.Contain("\"schema_version\":1"));
            Assert.That(json, Does.Contain("\"request_id\":\"req-1\""));
            Assert.That(json, Does.Contain("\"diagnostics\":[]"));
            Assert.That(json, Does.Contain("\"duration_ms\":12"));
            Assert.That(json, Does.Not.Contain("details"));
        }

        [Test]
        public void CorrelatesProjectOrdinallyAndBuildsCompleteMismatchEnvelope()
        {
            Assert.That(BridgeProtocol.TryParseInvoke(Valid, out var request, out var parseError), Is.True, parseError);
            Assert.That(BridgeProtocol.TryValidateProject(request, "game", 3, out var matchedError), Is.True);
            Assert.That(matchedError, Is.Empty);

            Assert.That(BridgeProtocol.TryValidateProject(request, "Game", 17, out var mismatch), Is.False);
            Assert.That(mismatch, Does.Contain("\"schema_version\":1"));
            Assert.That(mismatch, Does.Contain("\"request_id\":\"req-1\""));
            Assert.That(mismatch, Does.Contain("\"status\":\"error\""));
            Assert.That(mismatch, Does.Contain("\"code\":\"protocol_validation_error\""));
            Assert.That(mismatch, Does.Contain("\"diagnostics\":[]"));
            Assert.That(mismatch, Does.Contain("\"duration_ms\":17"));
        }

        [TestCase("{}", true)]
        [TestCase("{\"evidence\":{\"count\":0,\"flag\":false,\"reason\":\"ready\"},\"extra\":[null,true,0.5]}", true)]
        [TestCase("[]", false)]
        [TestCase("null", false)]
        [TestCase("{\"count\":0,\"count\":1}", false)]
        [TestCase("{\"count\":0,\"co\\u0075nt\":1}", false)]
        [TestCase("{\"evidence\":{\"flag\":false,\"flag\":true}}", false)]
        [TestCase("{\"flag\":false} trailing", false)]
        [TestCase("{\"count\":0.5garbage}", false)]
        [TestCase("{\"flag\":false,}", false)]
        [TestCase("{\"reason\":\"unterminated}", false)]
        public void ValidatesCompleteUniqueObjectsWithoutCoercingEvidence(string json, bool expected)
        {
            Assert.That(BridgeProtocol.IsValidUniqueObjectJson(json), Is.EqualTo(expected));
        }

        [Test]
        public void BoundsUniqueObjectEvidenceBeforeParsing()
        {
            Assert.That(BridgeProtocol.IsValidUniqueObjectJson(null), Is.False);
            Assert.That(BridgeProtocol.IsValidUniqueObjectJson(string.Empty), Is.False);
            Assert.That(BridgeProtocol.IsValidUniqueObjectJson("{\"value\":\"" + new string('x', BridgeProtocol.MaxInvokeBytes) + "\"}"), Is.False);
            Assert.That(BridgeProtocol.IsValidUniqueObjectJson("{\"nested\":" + new string('[', 40) + "0" + new string(']', 40) + "}"), Is.False);
        }

        [TestCase("{\"status\":\"running\",\"message\":\"pre_execute_refused\"}", true, false)]
        [TestCase("{\"status\":\"pre_execute_refused\"}", true, true)]
        [TestCase("{\"status\":\"pre_execute_\\u0072efused\"}", true, true)]
        [TestCase("{\"sta\\u0074us\":\"pre_execute_refused\"}", true, true)]
        [TestCase("{\"result\":\"pre_execute_refused\"}", true, true)]
        [TestCase("{\"lifecycle\":\"pre_execute_refused\"}", true, true)]
        [TestCase("{\"pre_execute_\\u0072efusal\":null}", true, true)]
        [TestCase("{\"status\":\"running\",\"status\":\"pre_execute_refused\"}", false, false)]
        [TestCase("{\"status\":\"running\"} trailing", false, false)]
        public void DetectsDecodedTopLevelMarkersBeforeLegacyEvidenceSelection(string json, bool valid, bool expectedMarker)
        {
            Assert.That(BridgeProtocol.TryHasObjectMarker(json, "pre_execute_refused", new[] { "status", "result", "lifecycle" }, "pre_execute_refusal", out var marker), Is.EqualTo(valid));
            Assert.That(marker, Is.EqualTo(expectedMarker));
        }

        static string Settings(string settings)
        {
            return Valid.Substring(0, Valid.Length - 1) + ",\"settings\":" + settings + "}";
        }

        static void AssertInvalid(string json)
        {
            Assert.That(BridgeProtocol.TryParseInvoke(json, out _, out _), Is.False);
        }
    }
}
