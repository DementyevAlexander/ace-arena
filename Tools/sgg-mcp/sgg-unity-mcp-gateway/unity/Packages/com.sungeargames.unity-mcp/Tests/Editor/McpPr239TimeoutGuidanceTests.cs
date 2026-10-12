using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Commands;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Tests
{
    // Pure metadata fixtures only. No cancel/status command, tracking cleanup,
    // TestRunner, filesystem artifact, popup, or historical receipt is exercised.
    public sealed class McpPr239TimeoutGuidanceTests
    {
        const string RunId = "test-pr239-timeout-guidance-fixture";
        const string ArtifactPath = "Library/SGGUnityMcp/TestRuns/test-pr239-timeout-guidance-fixture.json";
        const string PreviousStatus = "timeout";
        const string Reason = "synthetic_timeout_guidance";
        const string ResolveBeforeRepeating = "do_not_repeat_test.run_until_execution_state_is_resolved";
        const string PartialStateJson = "{\"status\":\"timeout\",\"total\":7,\"passed\":3,\"failed\":0,\"skipped\":0,\"success\":false,\"terminal_counts_authoritative\":false}";

        [TestCase(false, "absent")]
        [TestCase(false, "empty")]
        [TestCase(false, "partial")]
        [TestCase(true, "absent")]
        [TestCase(true, "empty")]
        [TestCase(true, "partial")]
        public void CancelFormatterPreservesMetadataAndWarnsAgainstUnresolvedReplay(bool artifactDeleted, string fixture)
        {
            var stateJson = fixture == "absent" ? null : fixture == "empty" ? "{}" : PartialStateJson;
            var inMemoryStateFound = fixture == "empty";
            var artifactFound = fixture == "partial";
            var cleanupPerformed = fixture == "empty";
            var method = PureMethod("TestRunCancelResultJson", typeof(string),
                typeof(string), typeof(string), typeof(string), typeof(bool), typeof(bool),
                typeof(bool), typeof(bool), typeof(string), typeof(string));

            var json = (string)method.Invoke(null, new object[]
            {
                RunId, ArtifactPath, PreviousStatus, inMemoryStateFound, artifactFound,
                cleanupPerformed, artifactDeleted, Reason, stateJson,
            });
            var projection = JsonUtility.FromJson<CancelProjection>(json);

            Assert.That(projection, Is.Not.Null);
            var status = artifactDeleted ? "forgotten" : "cancelled";
            var ready = !artifactDeleted && cleanupPerformed;
            var persisted = ready || (!artifactDeleted && artifactFound);
            var references = persisted ? new[] { ArtifactPath } : Array.Empty<string>();
            var actions = artifactDeleted
                ? new[] { "diagnostics.report_problem", ResolveBeforeRepeating }
                : new[] { "test.run.status", "job.artifact.read", "diagnostics.report_problem", ResolveBeforeRepeating };

            Assert.That(projection.command, Is.EqualTo("test.run.cancel"));
            Assert.That(projection.operation_id, Is.EqualTo(RunId));
            Assert.That(projection.run_id, Is.EqualTo(RunId));
            Assert.That(projection.status, Is.EqualTo(status));
            Assert.That(projection.result, Is.EqualTo(status));
            Assert.That(projection.previous_status, Is.EqualTo(PreviousStatus));
            Assert.That(projection.reason, Is.EqualTo(Reason));
            Assert.That(projection.cancel_requested, Is.True);
            Assert.That(projection.in_memory_state_found, Is.EqualTo(inMemoryStateFound));
            Assert.That(projection.artifact_found, Is.EqualTo(artifactFound));
            Assert.That(projection.cleanup_performed, Is.EqualTo(cleanupPerformed));
            Assert.That(projection.artifact_deleted, Is.EqualTo(artifactDeleted));
            Assert.That(projection.artifact_path, Is.EqualTo(ArtifactPath));
            Assert.That(projection.artifact_ref, Is.EqualTo(ArtifactPath));
            Assert.That(projection.artifact_ready, Is.EqualTo(ready));
            Assert.That(projection.artifact_persisted, Is.EqualTo(persisted));
            Assert.That(projection.artifact_refs, Is.EqualTo(references));
            Assert.That(projection.readable_with, Is.EqualTo("job.artifact.read"));
            Assert.That(projection.next_valid_actions, Is.EqualTo(actions));
            Assert.That(projection.next_valid_actions, Does.Not.Contain("test.run"));
            Assert.That(projection.warning, Does.Contain("tracking cleanup does not prove"));
            Assert.That(projection.warning, Does.Contain("Unity Test Runner stopped"));
            Assert.That(projection.warning, Does.Contain("Do not start another run until execution state is resolved."));

            // Pin all existing fields and the raw optional state. Only warning
            // wording and the exact guidance array are allowed to change here.
            Assert.That(json, Is.EqualTo(ExpectedCancelJson(status, inMemoryStateFound, artifactFound,
                cleanupPerformed, artifactDeleted, ready, persisted, references, actions, projection.warning, stateJson)));
            if (stateJson == null)
            {
                // JsonUtility can materialize an absent nested DTO. The exact
                // JSON equality above and absent key below witness omission,
                // independently of that CLR deserialization representation.
                Assert.That(json, Does.Not.Contain("\"state\":"));
            }
            else
            {
                Assert.That(projection.state, Is.Not.Null);
                Assert.That(projection.state.success, Is.False);
                Assert.That(projection.state.terminal_counts_authoritative, Is.False);
                Assert.That(projection.state.failed, Is.Zero);
                Assert.That(projection.state.skipped, Is.Zero);
                if (fixture == "partial")
                {
                    Assert.That(projection.state.status, Is.EqualTo("timeout"));
                    Assert.That(projection.state.total, Is.EqualTo(7));
                    Assert.That(projection.state.passed, Is.EqualTo(3));
                }
                else
                {
                    Assert.That(projection.state.status, Is.Null.Or.Empty);
                    Assert.That(projection.state.total, Is.Zero);
                    Assert.That(projection.state.passed, Is.Zero);
                }
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void TimeoutActionsTakePriorityOverPendingPublicationWithoutChangingState(bool artifactPersisted, bool dirtyScenes)
        {
            var state = State(false);
            state.Status = "timeout";
            state.Success = false;
            state.ArtifactPublicationState = "pending";
            state.PendingArtifactPublication = true;
            state.ArtifactPersisted = artifactPersisted;
            state.HasUnsavedSceneChangesAtStart = dirtyScenes;
            var before = Snapshot(state);
            var expected = new List<string> { "test.run.status" };
            if (artifactPersisted) expected.Add("job.artifact.read");
            expected.Add("diagnostics.report_problem");
            expected.Add(ResolveBeforeRepeating);
            if (dirtyScenes) expected.Add("editor.save_prompt.get");

            var actions = InvokeActions(state);

            Assert.That(actions, Is.EqualTo(expected));
            Assert.That(actions, Does.Not.Contain("test.run"));
            Assert.That(actions, Does.Not.Contain("test.run.cancel"));
            Assert.That(actions, Does.Not.Contain("editor.save_prompt.respond"));
            Assert.That(Snapshot(state), Is.EqualTo(before));
        }

        [TestCase("null")]
        [TestCase("dry_run")]
        [TestCase("running")]
        [TestCase("callback_lost")]
        [TestCase("pending_publication")]
        [TestCase("success")]
        [TestCase("failed")]
        public void NonTimeoutActionBranchesKeepTheirExactLegacyGuidance(string fixture)
        {
            BuildTestAutomationCommands.TestRunState state = fixture == "null" ? null : State(fixture == "dry_run");
            string[] expected;
            switch (fixture)
            {
                case "null":
                    expected = new[] { "test.run.status" };
                    break;
                case "dry_run":
                    state.Status = "timeout";
                    state.ArtifactPublicationState = "pending";
                    expected = new[] { "test.run", "test.list", "compile.status" };
                    break;
                case "running":
                    state.Status = "running";
                    state.ArtifactPublicationState = "pending";
                    expected = new[] { "test.run.status", "test.run.cancel" };
                    break;
                case "callback_lost":
                    state.Status = "callback_lost_after_playmode_exit";
                    state.ArtifactPublicationState = "pending";
                    expected = new[]
                    {
                        "job.artifact.read", "test.run.batchmode_fallback", "compile.status",
                        "logs.console.query", "test.run.cancel", "diagnostics.report_problem",
                    };
                    break;
                case "pending_publication":
                    state.Status = "completed";
                    state.ArtifactPublicationState = "pending";
                    state.ArtifactPersisted = true;
                    state.Success = true;
                    expected = new[] { "test.run.status", "job.artifact.read", "diagnostics.report_problem" };
                    break;
                case "success":
                    state.Status = "completed";
                    state.ArtifactPublicationState = "committed";
                    state.Success = true;
                    expected = new[] { "job.artifact.read", "artifact.metadata", "test.list" };
                    break;
                default:
                    state.Status = "failed";
                    state.ArtifactPublicationState = "not_started";
                    expected = new[] { "job.artifact.read", "compile.status", "logs.console.query", "test.list" };
                    break;
            }
            var before = Snapshot(state);

            var actions = InvokeActions(state);

            Assert.That(actions, Is.EqualTo(expected));
            Assert.That(actions, Does.Not.Contain(ResolveBeforeRepeating));
            Assert.That(Snapshot(state), Is.EqualTo(before));
        }

        static BuildTestAutomationCommands.TestRunState State(bool dryRun)
        {
            // Finished, cancellation, cleanup and execution flags stay at their
            // honest defaults; status alone is not evidence of runner shutdown.
            return new BuildTestAutomationCommands.TestRunState(RunId, "editmode", "Synthetic.TimeoutFixture", ArtifactPath, 60, dryRun)
            {
                Total = 7, Passed = 3, Failed = 0, Skipped = 0,
                TerminalCountsAuthoritative = false,
                CreatedAt = "2026-10-08T12:00:00.0000000Z",
                StartedAt = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc),
                LastUpdatedAt = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc),
            };
        }

        static List<string> InvokeActions(BuildTestAutomationCommands.TestRunState state)
        {
            var method = PureMethod("TestRunNextValidActions", typeof(List<string>), typeof(BuildTestAutomationCommands.TestRunState));
            return (List<string>)method.Invoke(null, new object[] { state });
        }

        static MethodInfo PureMethod(string name, Type returnType, params Type[] parameterTypes)
        {
            var method = typeof(BuildTestAutomationCommands).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            Assert.That(method.IsPrivate, Is.True);
            Assert.That(method.IsStatic, Is.True);
            Assert.That(method.ReturnType, Is.EqualTo(returnType));
            Assert.That(Array.ConvertAll(method.GetParameters(), parameter => parameter.ParameterType), Is.EqualTo(parameterTypes));
            return method;
        }

        static object[] Snapshot(BuildTestAutomationCommands.TestRunState state)
        {
            if (state == null) return Array.Empty<object>();
            return new object[]
            {
                state.RunId, state.Mode, state.Filter, state.ArtifactPath, state.MaxSeconds, state.DryRun,
                state.Status, state.ResultState, state.Success, state.Total, state.Passed, state.Failed, state.Skipped,
                state.TerminalCountsAuthoritative, state.PassCount, state.FailCount, state.SkipCount, state.InconclusiveCount,
                state.AssertCountAvailable, state.AssertCount, state.TestCaseCountAvailable, state.TestCaseCount,
                state.Finished, state.FinishedAt, state.ArtifactWritten, state.ArtifactPersisted,
                state.PendingArtifactPublication, state.ArtifactPublicationState, state.ArtifactPublicationAttempts,
                state.ArtifactPublicationErrorCode, state.ArtifactPublicationDeadlineUtc, state.NextArtifactPublicationAttempt,
                state.CancelRequested, state.CleanupPerformed, state.ExecuteDispatchCount, state.ExecuteCallReturned,
                state.HasUnsavedSceneChangesAtStart, state.DirtySceneCountAtStart,
                state.CreatedAt, state.StartedAt, state.LastUpdatedAt, state.Api, state.Callbacks,
            };
        }

        static string ExpectedCancelJson(string status, bool memory, bool found, bool cleanup, bool deleted,
            bool ready, bool persisted, string[] references, string[] actions, string warning, string stateJson)
        {
            return "{\"command\":\"test.run.cancel\",\"operation_id\":" + Quote(RunId)
                + ",\"run_id\":" + Quote(RunId) + ",\"status\":" + Quote(status) + ",\"result\":" + Quote(status)
                + ",\"cancel_requested\":true,\"cleanup_performed\":" + Bool(cleanup)
                + ",\"in_memory_state_found\":" + Bool(memory) + ",\"artifact_found\":" + Bool(found)
                + ",\"artifact_deleted\":" + Bool(deleted) + ",\"previous_status\":" + Quote(PreviousStatus)
                + ",\"artifact_path\":" + Quote(ArtifactPath) + ",\"artifact_ref\":" + Quote(ArtifactPath)
                + ",\"artifact_ready\":" + Bool(ready) + ",\"artifact_persisted\":" + Bool(persisted)
                + ",\"artifact_refs\":" + ArrayJson(references) + ",\"readable_with\":\"job.artifact.read\""
                + ",\"reason\":" + Quote(Reason) + ",\"warning\":" + Quote(warning)
                + ",\"next_valid_actions\":" + ArrayJson(actions)
                + (stateJson == null ? string.Empty : ",\"state\":" + stateJson) + "}";
        }

        static string ArrayJson(string[] values)
        {
            return "[" + string.Join(",", Array.ConvertAll(values, Quote)) + "]";
        }

        static string Quote(string value)
        {
            // The fixed fixture fields and generated warning contain ordinary
            // text; this is an expected-output encoder, not an input validator.
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "\"";
        }

        static string Bool(bool value) { return value ? "true" : "false"; }

        [Serializable]
        sealed class CancelProjection
        {
            public string command = string.Empty;
            public string operation_id = string.Empty;
            public string run_id = string.Empty;
            public string status = string.Empty;
            public string result = string.Empty;
            public bool cancel_requested = false;
            public bool cleanup_performed = false;
            public bool in_memory_state_found = false;
            public bool artifact_found = false;
            public bool artifact_deleted = false;
            public string previous_status = string.Empty;
            public string artifact_path = string.Empty;
            public string artifact_ref = string.Empty;
            public bool artifact_ready = false;
            public bool artifact_persisted = false;
            public string[] artifact_refs = null;
            public string readable_with = string.Empty;
            public string reason = string.Empty;
            public string warning = string.Empty;
            public string[] next_valid_actions = null;
            public StateProjection state = null;
        }

        [Serializable]
        sealed class StateProjection
        {
            public string status = string.Empty;
            public int total = 0;
            public int passed = 0;
            public int failed = 0;
            public int skipped = 0;
            public bool success = false;
            public bool terminal_counts_authoritative = false;
        }
    }
}
