using NUnit.Framework;
using SGG.UnityMCP.Editor.Commands;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class McpPr246EditorQuitPreflightRefusalTests
    {
        const string OperationId = "quit-20261012-001030-d9acb5d6";
        const string CallerArgsHash = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string ProcessStartUtc = "2026-10-12T00:00:00.0000000Z";

        [TestCase("batchmode")]
        [TestCase("compiling")]
        [TestCase("updating")]
        [TestCase("playmode")]
        [TestCase("unsaved_scene_changes")]
        [TestCase("confirm_close_required")]
        public void ExactRefusalBindsOriginalOperationAndOwnerWithoutIssuingClose(string reason)
        {
            var json = Build(Details(reason));
            var refusal = JsonUtil.ExtractObject(json, "editor_quit_preflight_refusal");
            Assert.That(JsonUtil.ExtractBool(json, "close_requested", true), Is.False);
            Assert.That(JsonUtil.ExtractInt(refusal, "schema_version", 0), Is.EqualTo(1));
            Assert.That(JsonUtil.ExtractString(refusal, "contract"), Is.EqualTo("sgg.unity_mcp.editor_quit_preflight_refusal.v1"));
            Assert.That(JsonUtil.ExtractString(refusal, "command"), Is.EqualTo("editor.quit"));
            Assert.That(JsonUtil.ExtractString(refusal, "operation_id"), Is.EqualTo(OperationId));
            Assert.That(JsonUtil.ExtractString(refusal, "caller_args_hash"), Is.EqualTo(CallerArgsHash));
            Assert.That(JsonUtil.ExtractString(refusal, "reason"), Is.EqualTo(reason));
            Assert.That(JsonUtil.ExtractString(refusal, "phase"), Is.EqualTo("preflight"));
            Assert.That(JsonUtil.ExtractString(refusal, "scope"), Is.EqualTo("this_invocation"));
            Assert.That(JsonUtil.ExtractBool(refusal, "binding_complete", false), Is.True);
            var owner = JsonUtil.ExtractObject(refusal, "owner");
            Assert.That(JsonUtil.ExtractString(owner, "project"), Is.EqualTo("sample"));
            Assert.That(JsonUtil.ExtractString(owner, "instance_id"), Is.EqualTo("sample-current"));
            Assert.That(JsonUtil.ExtractString(owner, "canonical_project_root"), Is.EqualTo("c:/work/sample"));
            Assert.That(JsonUtil.ExtractInt(owner, "unity_pid", 0), Is.EqualTo(12345));
            Assert.That(JsonUtil.ExtractString(owner, "unity_process_start_utc"), Is.EqualTo(ProcessStartUtc));
            AssertNoCloseOrCustodyClaims(refusal);
        }

        [Test]
        public void DirtySceneEvidenceIsPreservedWithoutInventingCleanliness()
        {
            var details = "{\"command\":\"editor.quit\",\"reason\":\"unsaved_scene_changes\",\"has_unsaved_scene_changes\":true,\"dirty_scene_count\":2,\"dirty_scenes\":[{\"path\":\"Assets/Dirty.unity\"}],\"hint\":\"Operator must decide.\"}";
            var json = Build(details);
            Assert.That(json, Does.StartWith(details.Substring(0, details.Length - 1) + ","));
            Assert.That(JsonUtil.ExtractBool(json, "has_unsaved_scene_changes", false), Is.True);
            Assert.That(JsonUtil.ExtractInt(json, "dirty_scene_count", 0), Is.EqualTo(2));
            Assert.That(JsonUtil.ExtractString(json, "hint"), Is.EqualTo("Operator must decide."));
            Assert.That(json, Does.Contain("Assets/Dirty.unity"));
            AssertNoCloseOrCustodyClaims(JsonUtil.ExtractObject(json, "editor_quit_preflight_refusal"));
        }

        [TestCase("{}")]
        [TestCase("{\"dry_run\":true}")]
        [TestCase("{\"dry_run\":false,\"confirm_close\":true}")]
        public void MissingReservedCorrelationIsExplicitlyUnbindable(string argsJson)
        {
            var json = Build(Details("playmode"), argsJson);
            var refusal = JsonUtil.ExtractObject(json, "editor_quit_preflight_refusal");
            Assert.That(JsonUtil.ExtractBool(refusal, "binding_complete", true), Is.False);
            Assert.That(JsonUtil.ExtractString(refusal, "operation_id"), Is.Empty);
            Assert.That(JsonUtil.ExtractString(refusal, "caller_args_hash"), Is.Empty);
            AssertNoCloseOrCustodyClaims(refusal);
        }

        [TestCase("operation_id", "quit-20261012-001030-D9ACB5D6")]
        [TestCase("operation_id", "quit-20261012-001030-d9acb5d6\n")]
        [TestCase("operation_id", "other-20261012-001030-d9acb5d6")]
        [TestCase("caller_args_hash", "sha256:short")]
        [TestCase("caller_args_hash", "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\n")]
        public void InvalidCorrelationIsNotTruncatedOrEchoedAsABinding(string field, string value)
        {
            var argsJson = Args(field == "operation_id" ? value : OperationId, field == "caller_args_hash" ? value : CallerArgsHash);
            var refusal = JsonUtil.ExtractObject(Build(Details("playmode"), argsJson), "editor_quit_preflight_refusal");
            Assert.That(JsonUtil.ExtractBool(refusal, "binding_complete", true), Is.False);
            Assert.That(JsonUtil.ExtractString(refusal, field), Is.Empty);
            AssertNoCloseOrCustodyClaims(refusal);
        }

        [TestCase("project")]
        [TestCase("instance")]
        [TestCase("root")]
        [TestCase("pid")]
        [TestCase("start")]
        public void IncompleteOwnerNeverClaimsABindableRefusal(string field)
        {
            var json = EditorStatusCommands.EditorQuitPreflightRefusalDetailsJson(
                Details("playmode"), Args(), field == "project" ? null : "sample",
                field == "instance" ? string.Empty : "sample-current",
                field == "root" ? string.Empty : "c:/work/sample", field == "pid" ? 0 : 12345,
                field == "start" ? string.Empty : ProcessStartUtc);
            var refusal = JsonUtil.ExtractObject(json, "editor_quit_preflight_refusal");
            Assert.That(JsonUtil.ExtractBool(refusal, "binding_complete", true), Is.False);
            Assert.That(JsonUtil.ExtractString(refusal, "operation_id"), Is.EqualTo(OperationId));
            AssertNoCloseOrCustodyClaims(refusal);
        }

        [TestCase("project", 129)]
        [TestCase("instance_id", 257)]
        [TestCase("canonical_project_root", 4097)]
        public void OversizedOwnerIdentityIsUnavailableRatherThanTruncated(string field, int length)
        {
            var value = new string('x', length);
            var json = EditorStatusCommands.EditorQuitPreflightRefusalDetailsJson(
                Details("playmode"), Args(), field == "project" ? value : "sample",
                field == "instance_id" ? value : "sample-current",
                field == "canonical_project_root" ? value : "c:/work/sample", 12345, ProcessStartUtc);
            var refusal = JsonUtil.ExtractObject(json, "editor_quit_preflight_refusal");
            Assert.That(JsonUtil.ExtractBool(refusal, "binding_complete", true), Is.False);
            Assert.That(JsonUtil.ExtractString(JsonUtil.ExtractObject(refusal, "owner"), field), Is.Empty);
            Assert.That(json.Length, Is.LessThan(1500));
        }

        [TestCase("2026-10-12T00:00:00.0000000+00:00")]
        [TestCase("2026-10-12T00:00:00.0000000")]
        [TestCase("not-a-process-generation")]
        public void NonUtcOrMalformedStartCannotClaimExactProcessGeneration(string startUtc)
        {
            var json = EditorStatusCommands.EditorQuitPreflightRefusalDetailsJson(
                Details("playmode"), Args(), "sample", "sample-current", "c:/work/sample", 12345, startUtc);
            var refusal = JsonUtil.ExtractObject(json, "editor_quit_preflight_refusal");
            Assert.That(JsonUtil.ExtractBool(refusal, "binding_complete", true), Is.False);
            Assert.That(JsonUtil.ExtractString(JsonUtil.ExtractObject(refusal, "owner"), "unity_process_start_utc"), Is.Empty);
        }

        [Test]
        public void OwnerControlCharactersAreNotEchoedAsIdentity()
        {
            var json = EditorStatusCommands.EditorQuitPreflightRefusalDetailsJson(
                Details("playmode"), Args(), "sample", "sample-current\nforeign", "c:/work/sample", -1, ProcessStartUtc);
            var refusal = JsonUtil.ExtractObject(json, "editor_quit_preflight_refusal");
            var owner = JsonUtil.ExtractObject(refusal, "owner");
            Assert.That(JsonUtil.ExtractBool(refusal, "binding_complete", true), Is.False);
            Assert.That(JsonUtil.ExtractString(owner, "instance_id"), Is.Empty);
            Assert.That(JsonUtil.ExtractInt(owner, "unity_pid", -1), Is.Zero);
            Assert.That(json, Does.Not.Contain("foreign"));
        }

        [TestCase("{\"command\":\"other.command\",\"reason\":\"playmode\"}")]
        [TestCase("{\"command\":\"editor.quit\",\"reason\":\"unrelated_unity_busy\"}")]
        [TestCase("{\"command\":\"editor.quit\"}")]
        [TestCase("{\"command\":\"editor.quit\",\"reason\":\"playmode\",\"close_requested\":true}")]
        [TestCase("{\"command\":\"editor.quit\",\"reason\":\"playmode\",\"editor_quit_preflight_refusal\":{\"contract\":\"other\"}}")]
        public void UnrelatedOrAlreadyQualifiedDetailsAreNeverReclassified(string details)
        {
            Assert.That(Build(details), Is.EqualTo(details));
        }

        static string Build(string details, string argsJson = null)
        {
            return EditorStatusCommands.EditorQuitPreflightRefusalDetailsJson(
                details, argsJson ?? Args(), "sample", "sample-current", "c:/work/sample", 12345, ProcessStartUtc);
        }

        static string Args(string operationId = OperationId, string callerArgsHash = CallerArgsHash)
        {
            return "{\"dry_run\":false,\"confirm_close\":true,\"_editor_quit_operation_id\":" + JsonUtil.String(operationId)
                + ",\"_editor_quit_caller_args_hash\":" + JsonUtil.String(callerArgsHash) + "}";
        }

        static string Details(string reason)
        {
            return "{\"command\":\"editor.quit\",\"reason\":" + JsonUtil.String(reason) + "}";
        }

        static void AssertNoCloseOrCustodyClaims(string refusal)
        {
            foreach (var field in new[] { "close_requested", "quit_lifecycle_started", "exit_scheduled", "editor_exit_invoked", "automatic_replay_attempted", "automatic_process_termination_attempted", "retained_native_custody_observed", "retained_native_close_supported" })
            {
                Assert.That(JsonUtil.ExtractBool(refusal, field, true), Is.False, field);
            }
        }
    }
}
