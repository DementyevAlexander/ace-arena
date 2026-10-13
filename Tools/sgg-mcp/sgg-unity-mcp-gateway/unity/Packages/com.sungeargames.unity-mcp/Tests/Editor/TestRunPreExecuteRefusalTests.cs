using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Commands;
using UnityEditor;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Tests
{
    // Synchronous synthetic prepared operations only: never invoke the real TestRunner.
    // Each fixture temporarily borrows the active marker and restores the outer run in finally.
    public sealed class TestRunPreExecuteRefusalTests
    {
        const string Refused = "pre_execute_refused";
        const int MaxFixtureArtifactBytes = 1024 * 1024;

        [TestCase("unity_reloading", "asset_update_active")]
        [TestCase("background_compilation_failed", "background_compilation_failed")]
        [TestCase("package_refresh_required", "package_refresh_required")]
        public void DroppedRefusalResponseLeavesTheExactLiveAndDiskOnlyStatus(string code, string reason)
        {
            using (var fixture = new PreparedFixture())
            {
                fixture.Dispatch(Refusal(code, reason)); // Deliberately discard the launch response.
                AssertRefused(fixture.StatusJson(), fixture, code, reason, "committed", Refused);
                Assert.That(fixture.ExecuteCount, Is.Zero);
                Assert.That(fixture.LastExecuteEntered, Is.False);
                fixture.ForgetOwnMemory();
                AssertRefused(fixture.StatusJson(), fixture, code, reason, "committed", Refused);
                Assert.That(fixture.OperationExists(), Is.True, "The persisted exact id must still reject duplicate admission.");
                Assert.That(fixture.Api.UnregisterCount, Is.EqualTo(1));
                Assert.That(fixture.ExecuteCount, Is.Zero);
            }
        }

        [Test]
        public void OriginalErrorDetailsAreNotRewrittenWhilePersistedRefusalMetadataIsBounded()
        {
            using (var fixture = new PreparedFixture())
            {
                var original = Refusal(new string('c', 200), new string('r', 400), new string('m', 3000));
                var result = fixture.Dispatch(original);
                Assert.That(result.Status, Is.EqualTo("error"));
                Assert.That(result.ErrorCode, Is.EqualTo(original.ErrorCode));
                Assert.That(result.ErrorMessage, Is.EqualTo(original.ErrorMessage));
                Assert.That(result.ErrorDetailsJson, Does.StartWith(original.ErrorDetailsJson.Substring(0, original.ErrorDetailsJson.LastIndexOf('}'))));
                var returned = Project(result.ErrorDetailsJson);
                Assert.That(returned.reason, Is.EqualTo(new string('r', 400)));
                Assert.That(returned.command_dispatched, Is.False);
                Assert.That(returned.operation_created, Is.True);
                Assert.That(returned.operation_id, Is.EqualTo(fixture.State.RunId));
                Assert.That(returned.artifact_path, Is.EqualTo(fixture.State.ArtifactPath));
                Assert.That(returned.artifact_ready, Is.True);
                Assert.That(returned.artifact_persisted, Is.True);
                Assert.That(returned.execute_dispatch_count, Is.Zero);
                Assert.That(returned.test_runner_execute_invoked, Is.False);
                Assert.That(returned.pre_execute_refusal.code.Length, Is.EqualTo(96));
                Assert.That(returned.pre_execute_refusal.reason.Length, Is.EqualTo(160));
                Assert.That(Project(fixture.StatusJson()).message.Length, Is.EqualTo(1000));
                var restored = BuildTestAutomationCommands.TestRunStateFromPersistedArtifact(fixture.State.RunId, fixture.State.ArtifactPath, fixture.DiskJson());
                Assert.That(restored.Finished, Is.True);
                Assert.That(restored.PreExecuteRefusal.Code, Is.EqualTo(returned.pre_execute_refusal.code));
                Assert.That(restored.PreExecuteRefusal.Reason, Is.EqualTo(returned.pre_execute_refusal.reason));
                Assert.That(restored.Success, Is.False);
                Assert.That(restored.TerminalCountsAuthoritative, Is.False);
            }
        }

        [Test]
        public void TerminalizationPrecedesCleanupAndLateCallbacksCannotReplaceTheRefusal()
        {
            using (var fixture = new PreparedFixture())
            {
                Assert.That(BuildTestAutomationCommands.IsCurrentTestRunCallback(fixture.State, 1), Is.True);
                fixture.Dispatch(Refusal());
                Assert.That(fixture.Api.GuardRejectedDuringCleanup, Is.True);
                Assert.That(fixture.Api.UnregisterCount, Is.EqualTo(1));
                Assert.That(PrivateField("s_activeTestRunId").GetValue(null), Is.EqualTo(string.Empty));
                Assert.That(SessionState.GetString(PreparedFixture.ActiveKey, string.Empty), Is.Empty);
                Assert.That(fixture.OperationExists(), Is.True);
                var before = fixture.StatusJson();
                fixture.Callbacks.TestFinished(new object());
                fixture.Callbacks.RunFinished(new object());
                Assert.That(fixture.StatusJson(), Is.EqualTo(before));
                Assert.That(BuildTestAutomationCommands.IsCurrentTestRunCallback(fixture.State, 1), Is.False);
                Assert.Throws<InvalidOperationException>(() => fixture.Dispatch(Refusal()));
                Assert.That(fixture.ExecuteCount, Is.Zero);
            }
        }

        [Test]
        public void RefusalClearsOnlyItsOwnActiveMarker()
        {
            using (var fixture = new PreparedFixture())
            {
                var replacementMarker = NewRunId();
                PrivateCall("SetActiveTestRunSession", replacementMarker);
                fixture.Dispatch(Refusal());
                Assert.That(PrivateField("s_activeTestRunId").GetValue(null), Is.EqualTo(replacementMarker));
                Assert.That(SessionState.GetString(PreparedFixture.ActiveKey, string.Empty), Is.EqualTo(replacementMarker));
                Assert.That(fixture.ExecuteCount, Is.Zero);
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void FailedTerminalPublicationRetainsRefusalButDoesNotRelabelTheEarlierDiskSnapshot(bool transient)
        {
            using (var fixture = new PreparedFixture())
            {
                var previousDisk = fixture.DiskJson();
                fixture.FailPublications(transient);
                var result = fixture.Dispatch(Refusal());
                var publication = transient ? "pending" : "failed";
                Assert.That(result.ErrorCode, Is.EqualTo("unity_reloading"));
                AssertRefused(result.ErrorDetailsJson, fixture, "unity_reloading", "asset_update_active", publication, "callbacks_registered");
                AssertRefused(fixture.StatusJson(), fixture, "unity_reloading", "asset_update_active", publication, "callbacks_registered");
                Assert.That(Project(result.ErrorDetailsJson).artifact_ready, Is.False);
                Assert.That(fixture.DiskJson(), Is.EqualTo(previousDisk));
                Assert.That(previousDisk, Does.Not.Contain("\"pre_execute_refusal\":"));
                Assert.That(Project(previousDisk).status, Is.EqualTo("running"));
                var restored = fixture.ReloadOwnRetainedPublication();
                Assert.That(restored.Api, Is.Null);
                Assert.That(restored.Callbacks, Is.Null);
                Assert.That(restored.CallbackReRegisteredAfterReload, Is.False);
                Assert.That(restored.PendingArtifactPublication, Is.EqualTo(transient));
                AssertRefused(fixture.StatusJson(), fixture, "unity_reloading", "asset_update_active", publication, "callbacks_registered");
                fixture.Callbacks.RunFinished(new object());
                Assert.That(fixture.State.Status, Is.EqualTo(Refused));
                Assert.That(fixture.ExecuteCount, Is.Zero);
                Assert.That(fixture.OperationExists(), Is.True);
            }
        }

        [Test]
        public void ContentionRetriesPublishTheSameRetainedOperationWithoutAnotherExecute()
        {
            using (var fixture = new PreparedFixture())
            {
                fixture.FailPublications(true, 2);
                fixture.Dispatch(Refusal());
                var deadline = fixture.State.ArtifactPublicationDeadlineUtc;
                fixture.ReloadOwnRetainedPublication();
                fixture.RetryPublication();
                Assert.That(fixture.PublicationCalls, Is.EqualTo(1));
                fixture.Advance(1);
                fixture.RetryPublication();
                Assert.That(fixture.PublicationCalls, Is.EqualTo(2));
                Assert.That(fixture.State.ArtifactPublicationDeadlineUtc, Is.EqualTo(deadline));
                fixture.Advance(1);
                fixture.RetryPublication();
                Assert.That(fixture.PublicationCalls, Is.EqualTo(3));
                Assert.That(fixture.State.ArtifactPublicationAttempts, Is.EqualTo(3));
                Assert.That(fixture.State.PendingArtifactPublication, Is.False);
                Assert.That(SessionState.GetString(PreparedFixture.RetainedPrefix + fixture.State.RunId, string.Empty), Is.Empty);
                AssertRefused(fixture.StatusJson(), fixture, "unity_reloading", "asset_update_active", "committed", Refused);
                Assert.That(fixture.ExecuteCount, Is.Zero);
                Assert.That(fixture.Api.UnregisterCount, Is.EqualTo(1));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PublicationExhaustionRetainsTerminalFailureWithoutResettingTheAttemptOrTimeBudget(bool expireWindow)
        {
            using (var fixture = new PreparedFixture())
            {
                fixture.FailPublications(true);
                fixture.Dispatch(Refusal());
                var deadline = fixture.State.ArtifactPublicationDeadlineUtc;
                fixture.Advance(expireWindow ? 9 : 1);
                fixture.RetryPublication();
                if (!expireWindow)
                {
                    for (var attempt = 2; attempt < 8; attempt++)
                    {
                        fixture.Advance(1);
                        fixture.RetryPublication();
                    }
                }
                Assert.That(fixture.State.ArtifactPublicationState, Is.EqualTo("failed"));
                Assert.That(fixture.State.PendingArtifactPublication, Is.False);
                Assert.That(fixture.State.ArtifactPublicationAttempts, Is.EqualTo(expireWindow ? 2 : 8));
                Assert.That(fixture.State.ArtifactPublicationDeadlineUtc, Is.EqualTo(deadline));
                var attempts = fixture.PublicationCalls;
                fixture.ReloadOwnRetainedPublication();
                fixture.Advance(20);
                fixture.RetryPublication();
                Assert.That(fixture.PublicationCalls, Is.EqualTo(attempts));
                AssertRefused(fixture.StatusJson(), fixture, "unity_reloading", "asset_update_active", "failed", "callbacks_registered");
                Assert.That(fixture.ExecuteCount, Is.Zero);
                Assert.That(fixture.OperationExists(), Is.True);
            }
        }

        [Test]
        public void ExpiredPublicationWindowAtReloadBecomesFailedWithoutAnotherPublicationOrExecute()
        {
            using (var fixture = new PreparedFixture())
            {
                fixture.FailPublications(true);
                fixture.Dispatch(Refusal());
                var deadline = fixture.State.ArtifactPublicationDeadlineUtc;
                fixture.Advance(9);
                fixture.ReloadOwnRetainedPublication();
                Assert.That(fixture.State.ArtifactPublicationDeadlineUtc, Is.EqualTo(deadline));
                Assert.That(fixture.State.ArtifactPublicationState, Is.EqualTo("failed"));
                Assert.That(fixture.State.ArtifactPublicationErrorCode, Is.EqualTo("publication_retry_window_exhausted"));
                fixture.RetryPublication();
                Assert.That(fixture.PublicationCalls, Is.EqualTo(1));
                Assert.That(fixture.ExecuteCount, Is.Zero);
                AssertRefused(fixture.StatusJson(), fixture, "unity_reloading", "asset_update_active", "failed", "callbacks_registered");
            }
        }

        [TestCase("created", true)]
        [TestCase("callbacks_registered", false)]
        public void CoordinatorRequiresThePreparedReceiptRatherThanInferringAdmissionFromZeroDispatch(string lifecycle, bool persisted)
        {
            using (var fixture = new PreparedFixture())
            {
                fixture.State.Lifecycle = lifecycle;
                fixture.State.ArtifactPersisted = persisted;
                var before = StableReceiptSnapshot(fixture.State);
                Assert.Throws<InvalidOperationException>(() => fixture.Dispatch(Refusal()));
                Assert.That(StableReceiptSnapshot(fixture.State), Is.EqualTo(before));
                Assert.That(fixture.GateCount, Is.Zero);
                Assert.That(fixture.ExecuteCount, Is.Zero);
            }
        }

        [TestCase(1, false)]
        [TestCase(0, true)]
        [TestCase(-1, false)]
        public void PreparedCoordinatorRejectsContraryExecutionEvidenceWithoutResettingIt(int dispatchCount, bool executeReturned)
        {
            using (var fixture = new PreparedFixture())
            {
                fixture.State.ExecuteDispatchCount = dispatchCount;
                fixture.State.ExecuteCallReturned = executeReturned;
                var before = StableReceiptSnapshot(fixture.State);
                Assert.Throws<InvalidOperationException>(() => fixture.Dispatch(Refusal()));
                Assert.That(fixture.State.ExecuteDispatchCount, Is.EqualTo(dispatchCount));
                Assert.That(fixture.State.ExecuteCallReturned, Is.EqualTo(executeReturned));
                Assert.That(StableReceiptSnapshot(fixture.State), Is.EqualTo(before));
                Assert.That(before, Does.Not.Contain("\"pre_execute_refusal\":"));
                Assert.That(fixture.GateCount, Is.Zero);
                Assert.That(fixture.ExecuteCount, Is.Zero);
            }
        }

        [TestCase("created", 0)]
        [TestCase("callbacks_registered", 0)]
        [TestCase("execute_dispatching", 1)]
        public void CrashSnapshotsWithoutTheExplicitRefusalStayUnresolved(string lifecycle, int dispatchCount)
        {
            using (var fixture = new PreparedFixture())
            {
                fixture.State.ExecuteDispatchCount = dispatchCount;
                fixture.Persist(lifecycle);
                var disk = fixture.DiskJson();
                var restored = BuildTestAutomationCommands.TestRunStateFromPersistedArtifact(fixture.State.RunId, fixture.State.ArtifactPath, disk);
                Assert.That(restored.PreExecuteRefusal, Is.Null);
                Assert.That(restored.Status, Is.EqualTo("running"));
                Assert.That(restored.ExecuteDispatchCount, Is.EqualTo(dispatchCount));
                PrivateCall("FinalizeInvalidRecoveredTestRun", restored);
                var json = BuildTestAutomationCommands.TestRunStateJson(restored);
                Assert.That(json, Does.Not.Contain("\"pre_execute_refusal\":"));
                Assert.That(restored.Status, Is.Not.EqualTo(Refused));
                Assert.That(restored.Success, Is.False);
                Assert.That(restored.TerminalCountsAuthoritative, Is.False);
                Assert.That(fixture.ExecuteCount, Is.Zero);
            }
        }

        [Test]
        public void SettledCoordinatorCommitsBeforeOneSyntheticExecuteAndDoesNotReplay()
        {
            using (var fixture = new PreparedFixture())
            {
                fixture.ExecuteAction = () =>
                {
                    var dispatch = Project(fixture.DiskJson());
                    Assert.That(dispatch.lifecycle, Is.EqualTo("execute_dispatching"));
                    Assert.That(dispatch.execute_dispatch_count, Is.EqualTo(1));
                    Assert.That(dispatch.test_runner_execute_invoked, Is.False);
                };
                Assert.That(fixture.Dispatch(null), Is.Null);
                Assert.That(fixture.ExecuteCount, Is.EqualTo(1));
                Assert.That(fixture.LastExecuteEntered, Is.True);
                Assert.That(fixture.State.ExecuteCallReturned, Is.True);
                Assert.That(Project(fixture.DiskJson()).lifecycle, Is.EqualTo("execute_invoked"));
                Assert.Throws<InvalidOperationException>(() => fixture.Dispatch(null));
                Assert.That(fixture.ExecuteCount, Is.EqualTo(1));
                Assert.That(fixture.GateCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void ThrowingSyntheticExecuteKeepsThePreInvocationCommitmentAndEnteredEvidence()
        {
            using (var fixture = new PreparedFixture())
            {
                fixture.ExecuteAction = () => { throw new InvalidOperationException("synthetic uncertain Execute"); };
                Assert.Throws<InvalidOperationException>(() => fixture.Dispatch(null));
                Assert.That(fixture.LastExecuteEntered, Is.True);
                Assert.That(fixture.State.ExecuteDispatchCount, Is.EqualTo(1));
                Assert.That(fixture.State.ExecuteCallReturned, Is.False);
                Assert.That(Project(fixture.DiskJson()).lifecycle, Is.EqualTo("execute_dispatching"));
                Assert.That(fixture.State.PreExecuteRefusal, Is.Null);
                Assert.That(fixture.ExecuteCount, Is.EqualTo(1));
            }
        }

        [TestCase("returned")]
        [TestCase("dispatched")]
        [TestCase("negative")]
        [TestCase("missing")]
        public void RetainedRefusalRejectsContradictoryOrMissingExecutionEvidence(string evidence)
        {
            using (var fixture = new PreparedFixture())
            {
                fixture.FailPublications(false);
                fixture.Dispatch(Refusal());
                var retained = fixture.RetainedJson();
                if (evidence == "returned") retained = retained.Replace("\"test_runner_execute_invoked\":false", "\"test_runner_execute_invoked\":true");
                else if (evidence == "missing") retained = retained.Replace("\"execute_dispatch_count\":0,", string.Empty);
                else retained = retained.Replace("\"execute_dispatch_count\":0", "\"execute_dispatch_count\":" + (evidence == "negative" ? "-1" : "1"));
                Assert.That(BuildTestAutomationCommands.TestRunStateFromRetainedPublicationSession(fixture.State.RunId, retained, fixture.Hooks), Is.Null);
                Assert.That(fixture.ExecuteCount, Is.Zero);
            }
        }

        [TestCase("execute_dispatch_count", "0.5")]
        [TestCase("execute_dispatch_count", "0.0")]
        [TestCase("execute_dispatch_count", "0e0")]
        [TestCase("execute_dispatch_count", "\"0\"")]
        [TestCase("execute_dispatch_count", "true")]
        [TestCase("execute_dispatch_count", "null")]
        [TestCase("execute_dispatch_count", "1")]
        [TestCase("execute_dispatch_count", "-1")]
        [TestCase("execute_dispatch_count", "missing")]
        [TestCase("operation_created", "false")]
        [TestCase("operation_created", "\"true\"")]
        [TestCase("operation_created", "missing")]
        [TestCase("terminal", "false")]
        [TestCase("terminal", "null")]
        [TestCase("terminal", "missing")]
        [TestCase("execute_dispatch_committed", "true")]
        [TestCase("execute_dispatch_committed", "\"false\"")]
        [TestCase("test_runner_execute_invocation_attempted", "true")]
        [TestCase("test_runner_execute_invocation_attempted", "null")]
        [TestCase("test_runner_execute_invoked", "true")]
        [TestCase("test_runner_execute_invoked", "\"false\"")]
        [TestCase("test_runner_execute_invoked", "missing")]
        [TestCase("terminal_counts_authoritative", "true")]
        [TestCase("terminal_counts_authoritative", "\"false\"")]
        [TestCase("success", "true")]
        [TestCase("success", "\"false\"")]
        [TestCase("test_result_success", "true")]
        public void RefusalRestoreRequiresExactTypedExecutionAndTerminalLiterals(string property, string literal)
        {
            using (var fixture = new PreparedFixture())
            {
                fixture.FailPublications(false);
                fixture.Dispatch(Refusal());
                var content = fixture.RetainedJson();
                var original = property == "execute_dispatch_count" ? "0" : property == "operation_created" || property == "terminal" ? "true" : "false";
                var field = JsonUtil.String(property) + ":" + original;
                Assert.That(content, Does.Contain(field));
                content = literal == "missing" ? content.Replace(field + ",", string.Empty) : content.Replace(field, JsonUtil.String(property) + ":" + literal);
                AssertInvalidRefusalDoesNotPublish(fixture, content);
            }
        }

        [TestCase("code", "missing")]
        [TestCase("code", "null")]
        [TestCase("code", "123")]
        [TestCase("code", "false")]
        [TestCase("code", "[]")]
        [TestCase("code", "\"\"")]
        [TestCase("code", "\" \"")]
        [TestCase("code", "oversized")]
        [TestCase("reason", "missing")]
        [TestCase("reason", "null")]
        [TestCase("reason", "123")]
        [TestCase("reason", "false")]
        [TestCase("reason", "[]")]
        [TestCase("reason", "\"\"")]
        [TestCase("reason", "\" \\t \"")]
        [TestCase("reason", "\"\\u0020\"")]
        [TestCase("reason", "oversized")]
        public void RefusalRestoreRejectsMissingBlankNonStringOrOversizedTypedFields(string property, string literal)
        {
            using (var fixture = new PreparedFixture())
            {
                fixture.FailPublications(false);
                fixture.Dispatch(Refusal());
                var content = fixture.RetainedJson();
                var fields = JsonUtil.ExtractObject(content, "pre_execute_refusal");
                var original = JsonUtil.String(property) + ":" + JsonUtil.String(property == "code" ? "unity_reloading" : "asset_update_active");
                string changed;
                if (literal == "missing") changed = property == "code" ? fields.Replace(original + ",", string.Empty) : fields.Replace("," + original, string.Empty);
                else changed = fields.Replace(original, JsonUtil.String(property) + ":" + (literal == "oversized" ? JsonUtil.String(new string('x', property == "code" ? 97 : 161)) : literal));
                Assert.That(changed, Is.Not.EqualTo(fields));
                AssertInvalidRefusalDoesNotPublish(fixture, content.Replace(fields, changed));
            }
        }

        [TestCase("status")]
        [TestCase("result")]
        [TestCase("lifecycle")]
        [TestCase("execute_dispatch_count")]
        [TestCase("test_runner_execute_invoked")]
        [TestCase("pre_execute_refusal")]
        [TestCase("code")]
        [TestCase("reason")]
        [TestCase("nested_unknown")]
        public void RefusalRestoreRejectsContradictoryDuplicateKeysAtEveryDepth(string property)
        {
            using (var fixture = new PreparedFixture())
            {
                fixture.FailPublications(false);
                fixture.Dispatch(Refusal());
                var content = fixture.RetainedJson();
                var fields = JsonUtil.ExtractObject(content, "pre_execute_refusal");
                if (property == "code" || property == "reason" || property == "nested_unknown")
                {
                    var addition = property == "nested_unknown" ? "\"ignored\":{\"count\":0,\"count\":1}" : JsonUtil.String(property) + ":123";
                    content = content.Replace(fields, fields.Substring(0, fields.Length - 1) + "," + addition + "}");
                }
                else if (property == "pre_execute_refusal")
                {
                    content = content.Replace("\"pre_execute_refusal\":" + fields, "\"pre_execute_refusal\":" + fields + ",\"pre_execute_refusal\":{}");
                }
                else
                {
                    var original = property == "execute_dispatch_count" ? "0" : property == "test_runner_execute_invoked" ? "false" : JsonUtil.String(Refused);
                    // Spaces keep a deliberately contradictory running marker out of the legacy substring recovery path,
                    // so a failing strict-reader assertion can never attach a real TestRunner callback in this fixture.
                    var contrary = property == "execute_dispatch_count" ? "1" : property == "test_runner_execute_invoked" ? "true" : JsonUtil.String("running");
                    var field = JsonUtil.String(property) + ":" + original;
                    content = content.Replace(field, field + "," + JsonUtil.String(property) + " : " + contrary);
                }
                AssertInvalidRefusalDoesNotPublish(fixture, content);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RefusalRestoreRejectsTrailingGarbageAndNonObjectDocuments(bool arrayRoot)
        {
            using (var fixture = new PreparedFixture())
            {
                fixture.FailPublications(false);
                fixture.Dispatch(Refusal());
                var content = fixture.RetainedJson();
                AssertInvalidRefusalDoesNotPublish(fixture, arrayRoot ? "[" + content + "]" : content + " trailing garbage");
            }
        }

        [TestCase("duplicate_status_without_dto")]
        [TestCase("escaped_status_value_without_dto")]
        [TestCase("escaped_presence_key")]
        [TestCase("escaped_status_key")]
        [TestCase("legacy_duplicate")]
        [TestCase("legacy_trailing")]
        public void MalformedOrDecodedRefusalMarkersCannotFallThroughToLegacyRecovery(string fixtureKind)
        {
            using (var fixture = new PreparedFixture())
            {
                fixture.FailPublications(false);
                fixture.Dispatch(Refusal());
                var legacy = fixture.DiskJson(); // The earlier callbacks receipt has no refusal DTO.
                Assert.That(legacy, Does.Not.Contain("\"pre_execute_refusal\":"));
                string content;
                if (fixtureKind == "duplicate_status_without_dto")
                    content = legacy.Replace("\"status\":\"running\"", "\"status\":\"running\",\"status\":\"pre_execute_refused\"");
                else if (fixtureKind == "escaped_status_value_without_dto")
                    content = legacy.Replace("\"status\":\"running\"", "\"status\":\"pre_execute_\\u0072efused\"");
                else if (fixtureKind == "escaped_presence_key")
                    content = legacy.Substring(0, legacy.Length - 1) + ",\"pre_execute_\\u0072efusal\":null}";
                else if (fixtureKind == "escaped_status_key")
                    content = legacy.Replace("\"status\":\"running\"", "\"st\\u0061tus\":\"pre_execute_refused\"");
                else if (fixtureKind == "legacy_duplicate")
                    content = legacy.Replace("\"execute_dispatch_count\":0", "\"execute_dispatch_count\":0,\"execute_dispatch_count\":1");
                else
                    content = legacy + " trailing garbage";
                Assert.That((bool)PrivateCall("RequiresStrictTestRunRefusalValidation", content), Is.True);
                AssertInvalidRefusalDoesNotPublish(fixture, content);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ValidLegacyCallbacksReceiptsKeepTheirExistingParsingWithoutRefusalRequirements(bool fractionalLegacyCounter)
        {
            using (var fixture = new PreparedFixture())
            {
                var legacy = fixture.DiskJson();
                if (fractionalLegacyCounter) legacy = legacy.Replace("\"execute_dispatch_count\":0", "\"execute_dispatch_count\":0.5");
                Assert.That((bool)PrivateCall("RequiresStrictTestRunRefusalValidation", legacy), Is.False);
                var restored = BuildTestAutomationCommands.TestRunStateFromPersistedArtifact(fixture.State.RunId, fixture.State.ArtifactPath, legacy);
                Assert.That(restored, Is.Not.Null);
                Assert.That(restored.Status, Is.EqualTo("running"));
                Assert.That(restored.ExecuteDispatchCount, Is.Zero, "Valid legacy numeric parsing is intentionally unchanged.");
                Assert.That(restored.PreExecuteRefusal, Is.Null);
                Assert.That(restored.Finished, Is.False);
                Assert.That(BuildTestAutomationCommands.NormalizePersistedTestRunArtifactForRead(legacy, fixture.State.RunId, fixture.State.ArtifactPath), Is.EqualTo(legacy));
                Assert.That(PrivateField("s_activeTestRunId").GetValue(null), Is.EqualTo(fixture.State.RunId));
                Assert.That(SessionState.GetString(PreparedFixture.ActiveKey, string.Empty), Is.EqualTo(fixture.State.RunId));
                Assert.That(fixture.Api.UnregisterCount, Is.Zero);
                Assert.That(fixture.PublicationCalls, Is.Zero);
                Assert.That(fixture.ExecuteCount, Is.Zero);
            }
        }

        [TestCase("values")]
        [TestCase("keys")]
        [TestCase("both")]
        public void CompleteRefusalReceiptsDecodeEscapedMarkerValuesAndPropertyKeys(string fixtureKind)
        {
            using (var fixture = new PreparedFixture())
            {
                fixture.FailPublications(false);
                fixture.Dispatch(Refusal());
                var content = fixture.RetainedJson();
                if (fixtureKind == "values" || fixtureKind == "both")
                    content = content.Replace(JsonUtil.String(Refused), "\"pre_execute_\\u0072efused\"");
                if (fixtureKind == "keys" || fixtureKind == "both")
                {
                    content = content.Replace("\"status\":", "\"st\\u0061tus\":")
                        .Replace("\"result\":", "\"res\\u0075lt\":")
                        .Replace("\"lifecycle\":", "\"life\\u0063ycle\":");
                }
                Assert.That((bool)PrivateCall("RequiresStrictTestRunRefusalValidation", content), Is.True);
                var restored = BuildTestAutomationCommands.TestRunStateFromPersistedArtifact(fixture.State.RunId, fixture.State.ArtifactPath, content);
                Assert.That(restored, Is.Not.Null);
                Assert.That(restored.Status, Is.EqualTo(Refused));
                Assert.That(restored.ResultState, Is.EqualTo(Refused));
                Assert.That(restored.Lifecycle, Is.EqualTo(Refused));
                Assert.That(restored.Finished, Is.True);
                var retained = BuildTestAutomationCommands.TestRunStateFromRetainedPublicationSession(fixture.State.RunId, content, fixture.Hooks);
                Assert.That(retained, Is.Not.Null);
                Assert.That(retained.Status, Is.EqualTo(Refused));
                Assert.That(retained.ResultState, Is.EqualTo(Refused));
                Assert.That(retained.Lifecycle, Is.EqualTo(Refused));
                Assert.That(retained.PreExecuteRefusal.Code, Is.EqualTo("unity_reloading"));
                Assert.That(retained.PreExecuteRefusal.Reason, Is.EqualTo("asset_update_active"));
                fixture.WriteOwnArtifact(content);
                fixture.ForgetOwnMemory();
                AssertRefused(fixture.StatusJson(), fixture, "unity_reloading", "asset_update_active", "failed", "callbacks_registered");
                Assert.That(fixture.DiskJson(), Is.EqualTo(content));
                Assert.That(fixture.ExecuteCount, Is.Zero);
                Assert.That(fixture.Api.UnregisterCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void ValidRefusalWithNestedRunningMetadataNeverEntersCallbackRecovery()
        {
            using (var fixture = new PreparedFixture())
            {
                fixture.Dispatch(Refusal());
                var content = fixture.DiskJson();
                content = content.Substring(0, content.Length - 1) + ",\"synthetic_nested_running\":{\"status\":\"running\"}}";
                fixture.WriteOwnArtifact(content);
                fixture.ForgetOwnMemory();
                AssertRefused(fixture.StatusJson(), fixture, "unity_reloading", "asset_update_active", "committed", Refused);
                Assert.That(fixture.DiskJson(), Is.EqualTo(content));
                Assert.That(fixture.ExecuteCount, Is.Zero);
                Assert.That(fixture.Api.UnregisterCount, Is.EqualTo(1));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CappedFixtureReaderAcceptsExactCapAndSplitUnicodeWithoutUsingStreamLength(bool nonSeeking)
        {
            var bytes = new byte[MaxFixtureArtifactBytes];
            for (var index = 0; index < bytes.Length - 2; index++) bytes[index] = (byte)'a';
            bytes[bytes.Length - 2] = 0xc3;
            bytes[bytes.Length - 1] = 0xa9;
            using (Stream stream = nonSeeking ? (Stream)new PartialReadStream(bytes, 4093) : new MemoryStream(bytes, false))
            {
                var text = ReadFixtureUtf8(stream);
                Assert.That(text.Length, Is.EqualTo(MaxFixtureArtifactBytes - 1));
                Assert.That(text[text.Length - 1], Is.EqualTo('é'));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CappedFixtureReaderRejectsMaxPlusOneBeforeAttemptingUtf8Decode(bool nonSeeking)
        {
            var bytes = new byte[MaxFixtureArtifactBytes + 1];
            bytes[0] = 0xff; // Would fail UTF-8 decoding if overflow were not checked first.
            using (Stream stream = nonSeeking ? (Stream)new PartialReadStream(bytes, 4093) : new MemoryStream(bytes, false))
            {
                Assert.Throws<InvalidDataException>(() => ReadFixtureUtf8(stream));
            }
        }

        [TestCase("partial")]
        [TestCase("invalid")]
        [TestCase("truncated")]
        public void CappedFixtureReaderHandlesNonSeekingPartialReadsAndRejectsMalformedUtf8(string fixture)
        {
            var bytes = fixture == "partial" ? new UTF8Encoding(false, true).GetBytes("partial € and é")
                : fixture == "invalid" ? new byte[] { 0xc3, 0x28 } : new byte[] { 0xe2, 0x82 };
            using (var stream = new PartialReadStream(bytes, 1))
            {
                if (fixture == "partial") Assert.That(ReadFixtureUtf8(stream), Is.EqualTo("partial € and é"));
                else Assert.Throws<DecoderFallbackException>(() => ReadFixtureUtf8(stream));
            }
        }

        static void AssertInvalidRefusalDoesNotPublish(PreparedFixture fixture, string content)
        {
            var publications = fixture.PublicationCalls;
            Assert.That(BuildTestAutomationCommands.TestRunStateFromPersistedArtifact(fixture.State.RunId, fixture.State.ArtifactPath, content), Is.Null);
            Assert.That(BuildTestAutomationCommands.TestRunStateFromRetainedPublicationSession(fixture.State.RunId, content, fixture.Hooks), Is.Null);
            Assert.That(BuildTestAutomationCommands.NormalizePersistedTestRunArtifactForRead(content, fixture.State.RunId, fixture.State.ArtifactPath), Is.Empty);
            fixture.WriteOwnArtifact(content);
            fixture.ForgetOwnMemory();
            PrivateCall("SetActiveTestRunSession", fixture.State.RunId);
            var result = fixture.StatusResult();
            Assert.That(result.Status, Is.EqualTo("error"));
            Assert.That(result.ErrorCode, Is.EqualTo("bridge_protocol_error"));
            Assert.That(result.ErrorDetailsJson, Does.Not.Contain("\"pre_execute_refusal\":"));
            Assert.That(PrivateField("s_activeTestRunId").GetValue(null), Is.EqualTo(fixture.State.RunId));
            Assert.That(SessionState.GetString(PreparedFixture.ActiveKey, string.Empty), Is.EqualTo(fixture.State.RunId));
            Assert.That(fixture.DiskJson(), Is.EqualTo(content), "Invalid evidence must not be repaired or replaced on read.");
            Assert.That(fixture.PublicationCalls, Is.EqualTo(publications));
            Assert.That(fixture.ExecuteCount, Is.Zero);
            Assert.That(fixture.Api.UnregisterCount, Is.EqualTo(1));
        }

        static string ReadFixtureUtf8(Stream stream)
        {
            var buffer = new byte[8192];
            using (var payload = new MemoryStream())
            {
                while (true)
                {
                    var count = stream.Read(buffer, 0, Math.Min(buffer.Length, MaxFixtureArtifactBytes + 1 - (int)payload.Length));
                    if (count == 0) break;
                    if (payload.Length + count > MaxFixtureArtifactBytes) throw new InvalidDataException("Synthetic test artifact exceeds the bounded byte limit.");
                    payload.Write(buffer, 0, count);
                }
                return new UTF8Encoding(false, true).GetString(payload.GetBuffer(), 0, (int)payload.Length);
            }
        }

        sealed class PartialReadStream : Stream
        {
            readonly MemoryStream m_stream;
            readonly int m_chunk;

            public PartialReadStream(byte[] bytes, int chunk) { m_stream = new MemoryStream(bytes, false); m_chunk = chunk; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) { return m_stream.Read(buffer, offset, Math.Min(count, m_chunk)); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            protected override void Dispose(bool disposing) { if (disposing) m_stream.Dispose(); base.Dispose(disposing); }
        }

        static CommandExecutionResult Refusal(string code = "unity_reloading", string reason = "asset_update_active", string message = "Synthetic final compilation gate refusal")
        {
            return CommandExecutionResult.Error(code, message,
                "{ \"reason\": " + JsonUtil.String(reason)
                    + ", \"command\":\"test.run\", \"command_dispatched\":false, \"test_runner_execute_invoked\":false"
                    + ", \"write_command_repeated\":false, \"write_replay_allowed\":false"
                    + ", \"compile_barrier\":{\"status\":\"pending\",\"opaque\":\"retain { braces } and \\\"quotes\\\"\"} }");
        }

        static void AssertRefused(string json, PreparedFixture fixture, string code, string reason, string publication, string lastPublished)
        {
            Assert.That(json, Does.Contain("\"pre_execute_refusal\":"));
            var projection = Project(json);
            Assert.That(projection.operation_created, Is.True);
            Assert.That(projection.terminal, Is.True);
            Assert.That(projection.operation_id, Is.EqualTo(fixture.State.RunId));
            Assert.That(projection.run_id, Is.EqualTo(fixture.State.RunId));
            Assert.That(projection.artifact_path, Is.EqualTo(fixture.State.ArtifactPath));
            Assert.That(projection.artifact_ref, Is.EqualTo(fixture.State.ArtifactPath));
            Assert.That(projection.artifact_schema_version, Is.EqualTo(3));
            Assert.That(projection.args_hash, Is.EqualTo(fixture.State.ArgsHash));
            Assert.That(projection.caller_args_hash, Is.EqualTo(fixture.State.CallerArgsHash));
            Assert.That(projection.status, Is.EqualTo(Refused));
            Assert.That(projection.result, Is.EqualTo(Refused));
            Assert.That(projection.lifecycle, Is.EqualTo(Refused));
            Assert.That(projection.pre_execute_refusal.code, Is.EqualTo(code));
            Assert.That(projection.pre_execute_refusal.reason, Is.EqualTo(reason));
            Assert.That(projection.artifact_publication_state, Is.EqualTo(publication));
            Assert.That(projection.last_published_lifecycle, Is.EqualTo(lastPublished));
            Assert.That(projection.artifact_persisted, Is.True, "An earlier receipt exists even when terminal publication failed.");
            Assert.That(projection.success, Is.False);
            Assert.That(projection.terminal_counts_authoritative, Is.False);
            Assert.That(projection.execute_dispatch_count, Is.Zero);
            Assert.That(projection.execute_dispatch_committed, Is.False);
            Assert.That(projection.test_runner_execute_invocation_attempted, Is.False);
            Assert.That(projection.test_runner_execute_invoked, Is.False);
            Assert.That(projection.write_command_repeated, Is.False);
            Assert.That(projection.write_replay_allowed, Is.False);
        }

        static string StableReceiptSnapshot(BuildTestAutomationCommands.TestRunState state)
        {
            // Serialization computes elapsed duration from wall time even when state is unchanged.
            // Preserve every other receipt field, including timestamps and execution evidence.
            return System.Text.RegularExpressions.Regex.Replace(BuildTestAutomationCommands.TestRunStateJson(state),
                @"(?<!\\)""duration_ms"":\d+", "\"duration_ms\":0");
        }

        static Projection Project(string json)
        {
            return JsonUtility.FromJson<Projection>(json);
        }

        static string NewRunId()
        {
            return "test-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        static FieldInfo PrivateField(string name)
        {
            return typeof(BuildTestAutomationCommands).GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
        }

        static object PrivateCall(string name, params object[] args)
        {
            return typeof(BuildTestAutomationCommands).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);
        }

        sealed class PreparedFixture : IDisposable
        {
            internal const string ActiveKey = "SGG.UnityMCP.BuildTestAutomation.activeTestRun";
            const string RetainedIndex = "SGG.UnityMCP.BuildTestAutomation.retainedTestRunPublications";
            internal const string RetainedPrefix = "SGG.UnityMCP.BuildTestAutomation.retainedTestRunPublication.";
            const string MissingSessionValue = "__missing_pre_execute_refusal_fixture__";
            readonly object m_gate;
            readonly Dictionary<string, BuildTestAutomationCommands.TestRunState> m_runs;
            readonly Dictionary<string, BuildTestAutomationCommands.TestRunState> m_retained;
            readonly string m_previousActive;
            readonly string m_previousActiveSession;
            readonly string m_previousRetainedIndex;
            readonly bool m_previousUpdateRegistered;
            readonly string m_absoluteArtifactPath;
            readonly string m_runId;
            bool m_ownsIdentity;
            bool m_disposed;
            DateTime m_utc = DateTime.UtcNow;
            double m_editorTime;
            int m_failedPublications;
            bool m_transient;

            public PreparedFixture()
            {
                m_gate = PrivateField("s_testRunsGate").GetValue(null);
                m_runs = (Dictionary<string, BuildTestAutomationCommands.TestRunState>)PrivateField("s_testRuns").GetValue(null);
                m_retained = (Dictionary<string, BuildTestAutomationCommands.TestRunState>)PrivateField("s_retainedTestRunPublications").GetValue(null);
                m_previousActive = (string)PrivateField("s_activeTestRunId").GetValue(null);
                m_previousActiveSession = SessionState.GetString(ActiveKey, MissingSessionValue);
                m_previousRetainedIndex = SessionState.GetString(RetainedIndex, MissingSessionValue);
                m_previousUpdateRegistered = (bool)PrivateField("s_testRunUpdateRegistered").GetValue(null);
                m_runId = NewRunId();
                State = new BuildTestAutomationCommands.TestRunState(m_runId, "editmode", "Synthetic.PreExecuteRefusalFixture", "Library/SGGUnityMcp/job-artifacts/test-runs/" + m_runId + ".json", 30, false)
                {
                    ArgsHash = "sha256:" + new string('a', 64),
                    CallerArgsHash = "sha256:" + new string('b', 64),
                    FilterResolutionIdentity = "sha256:" + new string('c', 64),
                    FilterResolutionIdentityStatus = "bound_before_execute",
                    FilterResolutionStatus = "resolved",
                    ResolvedFilterName = "Synthetic.PreExecuteRefusalFixture",
                    ResolvedFilterCandidateCount = 1,
                    TestAssembly = "Synthetic.PreExecuteRefusalAssembly",
                    CallbackRegistrationEpoch = 1,
                };
                m_absoluteArtifactPath = (string)PrivateCall("AbsoluteProjectPath", State.ArtifactPath);
                Hooks = new BuildTestAutomationCommands.TestRunPublicationHooks
                {
                    PublishArtifact = Publish,
                    UtcNow = () => m_utc,
                    EditorTime = () => m_editorTime,
                };
                Api = new SyntheticApi(State);
                Callbacks = (ISyntheticCallbacks)TestRunCallbacksProxy.Create(typeof(ISyntheticCallbacks), State);
                State.Api = Api;
                State.Callbacks = Callbacks;
                try
                {
                    lock (m_gate)
                    {
                        Assert.That(m_runs.ContainsKey(m_runId) || m_retained.ContainsKey(m_runId), Is.False);
                        Assert.That(File.Exists(m_absoluteArtifactPath) || File.Exists(m_absoluteArtifactPath + ".meta"), Is.False);
                        Assert.That(SessionState.GetString(RetainedPrefix + m_runId, MissingSessionValue), Is.EqualTo(MissingSessionValue));
                        m_ownsIdentity = true;
                        m_runs.Add(m_runId, State);
                    }
                    // Isolate only the index during this synchronous fixture; leave parent receipts untouched.
                    SessionState.EraseString(RetainedIndex);
                    PrivateCall("RegisterTestRunUpdate");
                    Persist("created");
                    Api.RegisterCallbacks(Callbacks);
                    Persist("callbacks_registered");
                    PrivateCall("SetActiveTestRunSession", m_runId);
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public BuildTestAutomationCommands.TestRunState State { get; private set; }
            public BuildTestAutomationCommands.TestRunPublicationHooks Hooks { get; }
            public SyntheticApi Api { get; }
            public ISyntheticCallbacks Callbacks { get; }
            public Action ExecuteAction { get; set; }
            public int ExecuteCount { get; private set; }
            public int GateCount { get; private set; }
            public int PublicationCalls { get; private set; }
            public bool LastExecuteEntered { get; private set; }

            public CommandExecutionResult Dispatch(CommandExecutionResult refusal)
            {
                var entered = false;
                try
                {
                    return BuildTestAutomationCommands.DispatchPreparedTestRun(State,
                        () => { GateCount++; return refusal; },
                        () => { ExecuteCount++; ExecuteAction?.Invoke(); }, out entered);
                }
                finally
                {
                    LastExecuteEntered = entered;
                }
            }

            public void Persist(string lifecycle)
            {
                Assert.That((bool)PrivateCall("PersistTestRunSnapshot", State, lifecycle), Is.True);
            }

            public void FailPublications(bool transient, int count = int.MaxValue)
            {
                m_transient = transient;
                m_failedPublications = count;
                State.PublicationHooks = Hooks;
            }

            BuildTestAutomationCommands.FixedArtifactPublicationResult Publish(string artifactPath, string content)
            {
                Assert.That(artifactPath, Is.EqualTo(State.ArtifactPath));
                PublicationCalls++;
                if (m_failedPublications-- > 0)
                {
                    return m_transient
                        ? BuildTestAutomationCommands.FixedArtifactPublicationResult.Transient("win32_32", "synthetic contention")
                        : BuildTestAutomationCommands.FixedArtifactPublicationResult.Permanent("synthetic_publication_failure", "synthetic permanent publication failure");
                }
                return (BuildTestAutomationCommands.FixedArtifactPublicationResult)PrivateCall("TryWriteFixedRootArtifactAtomic", artifactPath, content,
                    "Library/SGGUnityMcp/job-artifacts/test-runs", "Test run artifact");
            }

            public void Advance(double seconds)
            {
                m_utc = m_utc.AddSeconds(seconds);
                m_editorTime += seconds;
            }

            public void RetryPublication()
            {
                PrivateCall("RetryTestRunPublication", State);
            }

            public string DiskJson()
            {
                using (var stream = new FileStream(m_absoluteArtifactPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    return ReadFixtureUtf8(stream);
                }
            }

            public void WriteOwnArtifact(string content)
            {
                var result = (BuildTestAutomationCommands.FixedArtifactPublicationResult)PrivateCall("TryWriteFixedRootArtifactAtomic", State.ArtifactPath, content,
                    "Library/SGGUnityMcp/job-artifacts/test-runs", "Test run artifact");
                Assert.That(result.Committed, Is.True);
            }

            public string RetainedJson()
            {
                return SessionState.GetString(RetainedPrefix + m_runId, string.Empty);
            }

            public BuildTestAutomationCommands.TestRunState ReloadOwnRetainedPublication()
            {
                var restored = BuildTestAutomationCommands.TestRunStateFromRetainedPublicationSession(m_runId, RetainedJson(), Hooks);
                Assert.That(restored, Is.Not.Null);
                lock (m_gate)
                {
                    m_runs.Remove(m_runId);
                    m_retained[m_runId] = restored;
                }
                State = restored;
                Assert.That(restored.Filter, Is.EqualTo("Synthetic.PreExecuteRefusalFixture"));
                Assert.That(restored.TestAssembly, Is.EqualTo("Synthetic.PreExecuteRefusalAssembly"));
                Assert.That(restored.FilterResolutionIdentity, Is.EqualTo("sha256:" + new string('c', 64)));
                Assert.That(restored.FilterResolutionIdentityStatus, Is.EqualTo("bound_before_execute"));
                return restored;
            }

            public string StatusJson()
            {
                var result = StatusResult();
                Assert.That(result.Status, Is.EqualTo("ok"), result.ErrorMessage);
                return result.DataJson;
            }

            public CommandExecutionResult StatusResult()
            {
                return BuildTestAutomationCommands.TestRunStatus(new McpCommandContext(null, string.Empty, "synthetic-fixture",
                    "{\"operation_id\":" + JsonUtil.String(m_runId) + "}", "{}"));
            }

            public void ForgetOwnMemory()
            {
                lock (m_gate)
                {
                    m_runs.Remove(m_runId);
                    m_retained.Remove(m_runId);
                }
            }

            public bool OperationExists()
            {
                return (bool)PrivateCall("TestRunOperationExists", m_runId, State.ArtifactPath);
            }

            public void Dispose()
            {
                if (m_disposed) return;
                m_disposed = true;
                try
                {
                    if (m_ownsIdentity)
                    {
                        ForgetOwnMemory();
                        SessionState.EraseString(RetainedPrefix + m_runId);
                        if (File.Exists(m_absoluteArtifactPath)) File.Delete(m_absoluteArtifactPath);
                        if (File.Exists(m_absoluteArtifactPath + ".meta")) File.Delete(m_absoluteArtifactPath + ".meta");
                    }
                }
                finally
                {
                    lock (m_gate) PrivateField("s_activeTestRunId").SetValue(null, m_previousActive);
                    RestoreSession(ActiveKey, m_previousActiveSession);
                    RestoreSession(RetainedIndex, m_previousRetainedIndex);
                    var callback = (EditorApplication.CallbackFunction)Delegate.CreateDelegate(typeof(EditorApplication.CallbackFunction),
                        typeof(BuildTestAutomationCommands).GetMethod("UpdateTestRuns", BindingFlags.Static | BindingFlags.NonPublic));
                    if (m_previousUpdateRegistered)
                    {
                        if (!(bool)PrivateField("s_testRunUpdateRegistered").GetValue(null)) EditorApplication.update += callback;
                    }
                    else
                    {
                        EditorApplication.update -= callback;
                    }
                    PrivateField("s_testRunUpdateRegistered").SetValue(null, m_previousUpdateRegistered);
                }
            }

            static void RestoreSession(string key, string value)
            {
                if (value == MissingSessionValue) SessionState.EraseString(key);
                else SessionState.SetString(key, value);
            }
        }

        public interface ISyntheticCallbacks
        {
            void TestFinished(object result);
            void RunFinished(object result);
        }

        public sealed class SyntheticApi
        {
            readonly BuildTestAutomationCommands.TestRunState m_state;
            ISyntheticCallbacks m_callbacks;

            internal SyntheticApi(BuildTestAutomationCommands.TestRunState state) { m_state = state; }
            public int UnregisterCount { get; private set; }
            public bool GuardRejectedDuringCleanup { get; private set; }

            public void RegisterCallbacks(ISyntheticCallbacks callbacks) { m_callbacks = callbacks; }

            public void UnregisterCallbacks(object callbacks)
            {
                UnregisterCount++;
                GuardRejectedDuringCleanup = !BuildTestAutomationCommands.IsCurrentTestRunCallback(m_state, m_state.CallbackRegistrationEpoch);
                m_callbacks.RunFinished(new object());
            }
        }

        [Serializable]
        public sealed class RefusalProjection
        {
            public string code;
            public string reason;
        }

        [Serializable]
        public sealed class Projection
        {
            public bool operation_created;
            public bool terminal;
            public bool command_dispatched;
            public string reason;
            public string operation_id;
            public string run_id;
            public string status;
            public string result;
            public string lifecycle;
            public string artifact_path;
            public string artifact_ref;
            public int artifact_schema_version;
            public string args_hash;
            public string caller_args_hash;
            public bool artifact_ready;
            public bool artifact_persisted;
            public string artifact_publication_state;
            public string last_published_lifecycle;
            public int execute_dispatch_count;
            public bool execute_dispatch_committed;
            public bool test_runner_execute_invocation_attempted;
            public bool test_runner_execute_invoked;
            public bool terminal_counts_authoritative;
            public bool success;
            public bool write_command_repeated;
            public bool write_replay_allowed;
            public string message;
            public RefusalProjection pre_execute_refusal;
        }
    }
}
