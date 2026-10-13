using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Commands;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class McpPr213DynamicCSharpContinuityTests
    {
        readonly List<string> m_artifactPaths = new List<string>();
        static int s_scalarFixtureInvocations;

        [SetUp]
        public void SetUp()
        {
            DynamicCSharpReadonlyResultStore.ResetForTests();
        }

        [TearDown]
        public void TearDown()
        {
            DynamicCSharpReadonlyResultStore.ResetForTests();
            for (var index = 0; index < m_artifactPaths.Count; index++)
            {
                try { if (File.Exists(m_artifactPaths[index])) File.Delete(m_artifactPaths[index]); }
                catch { }
            }
            m_artifactPaths.Clear();
        }

        [Test]
        public void CompletedResultPersistsEveryPhaseAndExactIdentity()
        {
            var fixture = BeginOperation("return 42;");
            CompleteSuccessfulPhases(fixture.Operation);

            var terminal = fixture.Operation.PublishTerminal(CommandExecutionResult.Ok("{\"value\":42}"));
            DynamicCSharpReadonlyResultStore.MarkResponseWrite(fixture.Context.ArgsJson, "completed", 7);
            var status = DynamicCSharpReadonlyResultStore.Status(StatusContext(fixture.State, fixture.OperationId));

            Assert.That(terminal.Status, Is.EqualTo("ok"));
            Assert.That(terminal.DataJson, Does.Contain("\"operation_id\":\"" + fixture.OperationId + "\""));
            Assert.That(terminal.DataJson, Does.Contain("\"snippet_hash\":\"" + Sha256("return 42;") + "\""));
            Assert.That(status.Status, Is.EqualTo("ok"));
            Assert.That(status.DataJson, Does.Contain("\"terminal\":true"));
            Assert.That(status.DataJson, Does.Contain("\"result_available\":true"));
            Assert.That(status.DataJson, Does.Contain("\"command_result\":{\"status\":\"ok\",\"data\":{\"value\":42}}"));
            Assert.That(status.DataJson, Does.Contain("\"project_queue_admission\":{\"state\":\"completed\",\"duration_ms\":12}"));
            Assert.That(status.DataJson, Does.Contain("\"reference_resolution\":{\"state\":\"completed\""));
            Assert.That(status.DataJson, Does.Contain("\"compilation\":{\"state\":\"completed\""));
            Assert.That(status.DataJson, Does.Contain("\"main_thread_execution\":{\"state\":\"completed\""));
            Assert.That(status.DataJson, Does.Contain("\"result_serialization\":{\"state\":\"completed\""));
            Assert.That(status.DataJson, Does.Contain("\"result_publication\":{\"state\":\"completed\""));
            Assert.That(status.DataJson, Does.Contain("\"bridge_response_write\":{\"state\":\"completed\",\"duration_ms\":7}"));
            Assert.That(status.DataJson, Does.Contain("\"command_replayed\":false"));
        }

        [TestCase("full")]
        [TestCase("compact")]
        public void AnnotatedScalarResultRestoresExactStoredProjectionWithoutReexecution(string responseMode)
        {
            // Synthetic compiled-method/storage proof, not Roslyn execution or
            // recovery of a reported host's discarded string tail.
            const string code = "return new string('a', 248) + \"\\n\\n\\n\\n\" + new string('b', 1560);";
            var fixture = BeginOperation(code);
            CompleteSuccessfulPhases(fixture.Operation);
            s_scalarFixtureInvocations = 0;
            var resetOutput = typeof(EditorCSharpExecutionCommands).GetMethod("ResetOutput", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(resetOutput, Is.Not.Null);
            try
            {
                resetOutput.Invoke(null, new object[] { 6500 });
                var method = typeof(McpPr213DynamicCSharpContinuityTests).GetMethod(nameof(ReturnAnnotatedScalarFixture), BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(method, Is.Not.Null);
                var response = EditorCSharpExecutionCommands.InvokeCompiledMethod(method, true, responseMode);
                Assert.That(response.Status, Is.EqualTo("ok"));
                Assert.That(response.DataJson, Does.Contain("\"returned_count\":252"));
                Assert.That(response.DataJson, Does.Contain("\"reason\":\"max_string_json_content_bytes\",\"applied_limit\":256"));
                var storedResult = "{\"status\":\"ok\",\"data\":" + response.DataJson + "}";
                var terminal = fixture.Operation.PublishTerminal(response);
                Assert.That(terminal.Status, Is.EqualTo("ok"));
                DynamicCSharpReadonlyResultStore.MarkResponseWrite(fixture.Context.ArgsJson, "completed", 7);
                Assert.That(File.ReadAllText(FullArtifactPath(fixture.OperationId)), Does.Contain("\"command_result_hash\":\"" + Sha256(storedResult) + "\""));
                DynamicCSharpReadonlyResultStore.ResetForTests();
                var status = DynamicCSharpReadonlyResultStore.Status(StatusContext(fixture.State, fixture.OperationId));
                Assert.That(status.Status, Is.EqualTo("ok"));
                Assert.That(status.DataJson, Does.Contain("\"command_result\":" + storedResult));
                Assert.That(status.DataJson, Does.Contain("\"operation_id\":\"" + fixture.OperationId + "\""));
                Assert.That(status.DataJson, Does.Contain("\"snippet_hash\":\"" + Sha256(code) + "\""));
                Assert.That(status.DataJson, Does.Contain("\"caller_args_hash\":\"" + Sha256("caller-args") + "\""));
                Assert.That(status.DataJson, Does.Contain("\"bridge_response_write\":{\"state\":\"completed\",\"duration_ms\":7}"));
                Assert.That(status.DataJson, Does.Contain("\"command_replayed\":false"));
                Assert.That(s_scalarFixtureInvocations, Is.EqualTo(1));
            }
            finally
            {
                s_scalarFixtureInvocations = 0;
                resetOutput.Invoke(null, new object[] { 4000 });
            }
        }

        static object ReturnAnnotatedScalarFixture()
        {
            s_scalarFixtureInvocations++;
            return new string('a', 248) + "\n\n\n\n" + new string('b', 1560);
        }

        [Test]
        public void InProgressReceiptExposesPhaseWithoutManufacturingResult()
        {
            var fixture = BeginOperation("return 42;");
            Assert.That(fixture.Operation.TryStartPhase("reference_resolution", out var phaseError), Is.True, phaseError == null ? string.Empty : phaseError.ErrorMessage);

            var status = DynamicCSharpReadonlyResultStore.Status(StatusContext(fixture.State, fixture.OperationId));

            Assert.That(status.Status, Is.EqualTo("ok"));
            Assert.That(status.DataJson, Does.Contain("\"status\":\"running\""));
            Assert.That(status.DataJson, Does.Contain("\"terminal\":false"));
            Assert.That(status.DataJson, Does.Contain("\"result_available\":false"));
            Assert.That(status.DataJson, Does.Contain("\"reference_resolution\":{\"state\":\"running\""));
            Assert.That(status.DataJson, Does.Contain("\"command_result\":null"));
        }

        [Test]
        public void PhasePublicationFailurePersistsTerminalFailureWithoutReplay()
        {
            var fixture = BeginOperation("return 42;");
            CompleteSuccessfulPhase(fixture.Operation, "reference_resolution");
            CompleteSuccessfulPhase(fixture.Operation, "compilation");
            Assert.That(fixture.Operation.TryStartPhase("main_thread_execution", out var phaseError), Is.True, phaseError == null ? string.Empty : phaseError.ErrorMessage);
            DynamicCSharpReadonlyResultStore.FailNextPublicationsForTests(1);

            var finished = fixture.Operation.TryFinishPhase("main_thread_execution", 3, true, out phaseError);

            Assert.That(finished, Is.False);
            Assert.That(phaseError.ErrorCode, Is.EqualTo("dynamic_csharp_result_unavailable"));
            Assert.That(phaseError.ErrorDetailsJson, Does.Contain("\"reason\":\"artifact_publication_failed\""));
            DynamicCSharpReadonlyResultStore.ResetForTests();
            var status = DynamicCSharpReadonlyResultStore.Status(StatusContext(fixture.State, fixture.OperationId));
            Assert.That(status.Status, Is.EqualTo("ok"));
            Assert.That(status.DataJson, Does.Contain("\"status\":\"failed\""));
            Assert.That(status.DataJson, Does.Contain("\"terminal\":true"));
            Assert.That(status.DataJson, Does.Contain("\"main_thread_execution\":{\"state\":\"completed\",\"duration_ms\":3}"));
            Assert.That(status.DataJson, Does.Contain("\"result_serialization\":{\"state\":\"not_started\""));
            Assert.That(status.DataJson, Does.Contain("\"result_publication\":{\"state\":\"failed\""));
            Assert.That(status.DataJson, Does.Contain("\"reason\":\"artifact_publication_failed\""));
            Assert.That(status.DataJson, Does.Contain("\"command_replayed\":false"));
        }

        [Test]
        public void InitialPublicationFailureRetainsTerminalExactStatus()
        {
            var operationId = "csharp-20260922-" + DateTime.UtcNow.ToString("HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var state = State("game-current");
            var code = "return 42;";
            var argsJson = "{\"code\":" + JsonUtil.String(code)
                + ",\"_dynamic_csharp_operation_id\":" + JsonUtil.String(operationId)
                + ",\"_dynamic_csharp_caller_args_hash\":" + JsonUtil.String(Sha256("caller-args"))
                + ",\"_dynamic_csharp_queue_wait_ms\":12}";
            var context = new McpCommandContext(state, "request", "game", argsJson, "{}");
            m_artifactPaths.Add(FullArtifactPath(operationId));
            DynamicCSharpReadonlyResultStore.FailNextPublicationsForTests(5);

            var began = DynamicCSharpReadonlyResultStore.TryBegin(context, code, out var operation, out var error);
            DynamicCSharpReadonlyResultStore.MarkResponseWrite(argsJson, "running", 0);
            var status = DynamicCSharpReadonlyResultStore.Status(StatusContext(state, operationId));

            Assert.That(began, Is.False);
            Assert.That(operation, Is.Null);
            Assert.That(error.ErrorCode, Is.EqualTo("dynamic_csharp_result_unavailable"));
            Assert.That(error.ErrorDetailsJson, Does.Contain("\"execution_may_have_started\":false"));
            Assert.That(File.Exists(FullArtifactPath(operationId)), Is.False);
            Assert.That(status.Status, Is.EqualTo("ok"));
            Assert.That(status.DataJson, Does.Contain("\"status\":\"failed\""));
            Assert.That(status.DataJson, Does.Contain("\"terminal\":true"));
            Assert.That(status.DataJson, Does.Contain("\"reference_resolution\":{\"state\":\"not_started\""));
            Assert.That(status.DataJson, Does.Contain("\"result_publication\":{\"state\":\"failed\""));
            Assert.That(status.DataJson, Does.Contain("\"bridge_response_write\":{\"state\":\"running\""));
            Assert.That(status.DataJson, Does.Contain("\"execution_may_have_started\":false"));
            Assert.That(status.DataJson, Does.Contain("\"command_replayed\":false"));
        }

        [Test]
        public void TerminalPublicationFailurePersistsFailureInsteadOfOriginalResult()
        {
            var fixture = BeginOperation("return 42;");
            CompleteSuccessfulPhases(fixture.Operation);
            DynamicCSharpReadonlyResultStore.FailNextPublicationsForTests(1);

            var terminal = fixture.Operation.PublishTerminal(CommandExecutionResult.Ok("{\"value\":42}"));

            Assert.That(terminal.Status, Is.EqualTo("error"));
            Assert.That(terminal.ErrorCode, Is.EqualTo("dynamic_csharp_result_unavailable"));
            DynamicCSharpReadonlyResultStore.ResetForTests();
            var status = DynamicCSharpReadonlyResultStore.Status(StatusContext(fixture.State, fixture.OperationId));
            Assert.That(status.Status, Is.EqualTo("ok"));
            Assert.That(status.DataJson, Does.Contain("\"status\":\"failed\""));
            Assert.That(status.DataJson, Does.Contain("\"terminal\":true"));
            Assert.That(status.DataJson, Does.Contain("\"result_publication\":{\"state\":\"failed\""));
            Assert.That(status.DataJson, Does.Contain("\"reason\":\"artifact_publication_failed\""));
            Assert.That(status.DataJson, Does.Not.Contain("\"value\":42"));
        }

        [Test]
        public void ExactStatusUsesTerminalActiveReceiptWhilePublicationRemainsUnavailable()
        {
            var fixture = BeginOperation("return 42;");
            CompleteSuccessfulPhase(fixture.Operation, "reference_resolution");
            CompleteSuccessfulPhase(fixture.Operation, "compilation");
            Assert.That(fixture.Operation.TryStartPhase("main_thread_execution", out var phaseError), Is.True, phaseError == null ? string.Empty : phaseError.ErrorMessage);
            DynamicCSharpReadonlyResultStore.FailNextPublicationsForTests(3);

            Assert.That(fixture.Operation.TryFinishPhase("main_thread_execution", 3, true, out phaseError), Is.False);
            var durableBeforeStatus = File.ReadAllText(FullArtifactPath(fixture.OperationId));
            var replacementStatus = DynamicCSharpReadonlyResultStore.Status(StatusContext(State("game-replacement"), fixture.OperationId));
            var status = DynamicCSharpReadonlyResultStore.Status(StatusContext(fixture.State, fixture.OperationId));

            Assert.That(durableBeforeStatus, Does.Contain("\"terminal\":false"));
            Assert.That(durableBeforeStatus, Does.Contain("\"main_thread_execution\":{\"state\":\"running\""));
            Assert.That(replacementStatus.Status, Is.EqualTo("error"));
            Assert.That(replacementStatus.ErrorCode, Is.EqualTo("dynamic_csharp_result_owner_mismatch"));
            Assert.That(status.Status, Is.EqualTo("ok"));
            Assert.That(status.DataJson, Does.Contain("\"status\":\"failed\""));
            Assert.That(status.DataJson, Does.Contain("\"terminal\":true"));
            Assert.That(status.DataJson, Does.Contain("\"result_publication\":{\"state\":\"failed\""));
            Assert.That(status.DataJson, Does.Contain("\"command_replayed\":false"));
        }

        [Test]
        public void PreflightFailurePersistsAsTerminalReferenceFailure()
        {
            var fixture = BeginOperation("return 42;");
            var terminal = fixture.Operation.PublishFailedPhase("reference_resolution", CommandExecutionResult.Error("dependency_missing", "Roslyn unavailable"));

            var status = DynamicCSharpReadonlyResultStore.Status(StatusContext(fixture.State, fixture.OperationId));

            Assert.That(terminal.Status, Is.EqualTo("error"));
            Assert.That(terminal.ErrorDetailsJson, Does.Contain("\"operation_id\":\"" + fixture.OperationId + "\""));
            Assert.That(status.Status, Is.EqualTo("ok"));
            Assert.That(status.DataJson, Does.Contain("\"status\":\"failed\""));
            Assert.That(status.DataJson, Does.Contain("\"reference_resolution\":{\"state\":\"failed\""));
            Assert.That(status.DataJson, Does.Contain("\"compilation\":{\"state\":\"not_started\""));
            Assert.That(status.DataJson, Does.Contain("\"command_result\":{\"status\":\"error\""));
        }

        [Test]
        public void ResponseWriteRequiresExactCallerAndSnippetBinding()
        {
            var fixture = BeginOperation("return 42;");
            CompleteSuccessfulPhases(fixture.Operation);
            fixture.Operation.PublishTerminal(CommandExecutionResult.Ok("{\"value\":42}"));
            var mismatchedArgs = fixture.Context.ArgsJson.Replace(Sha256("caller-args"), Sha256("different-caller"));

            DynamicCSharpReadonlyResultStore.MarkResponseWrite(mismatchedArgs, "completed", 7);
            var status = DynamicCSharpReadonlyResultStore.Status(StatusContext(fixture.State, fixture.OperationId));

            Assert.That(status.Status, Is.EqualTo("ok"));
            Assert.That(status.DataJson, Does.Contain("\"bridge_response_write\":{\"state\":\"not_started\",\"duration_ms\":0}"));
        }

        [Test]
        public void ResponseWritePublicationFailureReplacesSuccessWithTerminalFailure()
        {
            var fixture = BeginOperation("return 42;");
            CompleteSuccessfulPhases(fixture.Operation);
            Assert.That(fixture.Operation.PublishTerminal(CommandExecutionResult.Ok("{\"value\":42}")).Status, Is.EqualTo("ok"));
            DynamicCSharpReadonlyResultStore.MarkResponseWrite(fixture.Context.ArgsJson, "running", 0);
            DynamicCSharpReadonlyResultStore.FailNextPublicationsForTests(1);

            DynamicCSharpReadonlyResultStore.MarkResponseWrite(fixture.Context.ArgsJson, "completed", 7);
            var status = DynamicCSharpReadonlyResultStore.Status(StatusContext(fixture.State, fixture.OperationId));

            Assert.That(status.Status, Is.EqualTo("ok"));
            Assert.That(status.DataJson, Does.Contain("\"status\":\"failed\""));
            Assert.That(status.DataJson, Does.Contain("\"result_publication\":{\"state\":\"failed\""));
            Assert.That(status.DataJson, Does.Contain("\"bridge_response_write\":{\"state\":\"completed\",\"duration_ms\":7}"));
            Assert.That(status.DataJson, Does.Contain("\"reason\":\"artifact_publication_failed\""));
            Assert.That(status.DataJson, Does.Not.Contain("\"value\":42"));
        }

        [Test]
        public void OwnerReplacementFailsTypedWithoutReturningStoredResult()
        {
            var fixture = BeginOperation("return 42;");
            CompleteSuccessfulPhases(fixture.Operation);
            fixture.Operation.PublishTerminal(CommandExecutionResult.Ok("{\"value\":42}"));
            var replacement = State("game-replacement");

            var status = DynamicCSharpReadonlyResultStore.Status(StatusContext(replacement, fixture.OperationId));

            Assert.That(status.Status, Is.EqualTo("error"));
            Assert.That(status.ErrorCode, Is.EqualTo("dynamic_csharp_result_owner_mismatch"));
            Assert.That(status.ErrorDetailsJson, Does.Contain("\"instance_match\":false"));
            Assert.That(status.ErrorDetailsJson, Does.Contain("\"command_replayed\":false"));
            Assert.That(status.ErrorDetailsJson, Does.Not.Contain("\"value\":42"));
        }

        [Test]
        public void TamperedResultFailsIntegrityValidation()
        {
            var fixture = BeginOperation("return 42;");
            CompleteSuccessfulPhases(fixture.Operation);
            fixture.Operation.PublishTerminal(CommandExecutionResult.Ok("{\"value\":42}"));
            var artifactPath = FullArtifactPath(fixture.OperationId);
            var content = File.ReadAllText(artifactPath);
            File.WriteAllText(artifactPath, content.Replace("{\\\"value\\\":42}", "{\\\"value\\\":43}"), new UTF8Encoding(false));

            var status = DynamicCSharpReadonlyResultStore.Status(StatusContext(fixture.State, fixture.OperationId));

            Assert.That(status.Status, Is.EqualTo("error"));
            Assert.That(status.ErrorCode, Is.EqualTo("dynamic_csharp_result_invalid"));
            Assert.That(status.ErrorDetailsJson, Does.Contain("\"reason\":\"result_integrity_invalid\""));
        }

        [Test]
        public void MalformedActiveArtifactFailsClosedWithoutRepair()
        {
            var fixture = BeginOperation("return 42;");
            CompleteSuccessfulPhases(fixture.Operation);
            fixture.Operation.PublishTerminal(CommandExecutionResult.Ok("{\"value\":42}"));
            var artifactPath = FullArtifactPath(fixture.OperationId);
            File.WriteAllBytes(artifactPath, new byte[] { 0xff });

            var status = DynamicCSharpReadonlyResultStore.Status(StatusContext(fixture.State, fixture.OperationId));

            Assert.That(status.Status, Is.EqualTo("error"));
            Assert.That(status.ErrorCode, Is.EqualTo("dynamic_csharp_result_invalid"));
            Assert.That(status.ErrorDetailsJson, Does.Contain("\"reason\":\"artifact_encoding_invalid\""));
            Assert.That(File.ReadAllBytes(artifactPath), Is.EqualTo(new byte[] { 0xff }));
        }

        [Test]
        public void DifferentBodiesUseFreshRoslynAssemblyIdentities()
        {
            var first = EditorCSharpExecutionCommands.CreateDynamicAssemblyName();
            var second = EditorCSharpExecutionCommands.CreateDynamicAssemblyName();

            Assert.That(first, Does.StartWith("SGGUnityMcpDynamic_"));
            Assert.That(second, Does.StartWith("SGGUnityMcpDynamic_"));
            Assert.That(first, Is.Not.EqualTo(second));
        }

        OperationFixture BeginOperation(string code)
        {
            var operationId = "csharp-20260922-" + DateTime.UtcNow.ToString("HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var state = State("game-current");
            var argsJson = "{\"code\":" + JsonUtil.String(code)
                + ",\"_dynamic_csharp_operation_id\":" + JsonUtil.String(operationId)
                + ",\"_dynamic_csharp_caller_args_hash\":" + JsonUtil.String(Sha256("caller-args"))
                + ",\"_dynamic_csharp_queue_wait_ms\":12}";
            var context = new McpCommandContext(state, "request", "game", argsJson, "{}");
            Assert.That(DynamicCSharpReadonlyResultStore.TryBegin(context, code, out var operation, out var error), Is.True, error == null ? string.Empty : error.ErrorMessage + " " + error.ErrorDetailsJson);
            m_artifactPaths.Add(FullArtifactPath(operationId));
            return new OperationFixture(operationId, state, context, operation);
        }

        static void CompleteSuccessfulPhases(DynamicCSharpReadonlyOperation operation)
        {
            foreach (var phase in new[] { "reference_resolution", "compilation", "main_thread_execution", "result_serialization" })
            {
                CompleteSuccessfulPhase(operation, phase);
            }
        }

        static void CompleteSuccessfulPhase(DynamicCSharpReadonlyOperation operation, string phase)
        {
            Assert.That(operation.TryStartPhase(phase, out var startError), Is.True, startError == null ? string.Empty : startError.ErrorMessage);
            Assert.That(operation.TryFinishPhase(phase, 1, true, out var finishError), Is.True, finishError == null ? string.Empty : finishError.ErrorMessage);
        }

        static BridgeRuntimeState State(string instanceId)
        {
            var state = new BridgeRuntimeState { InstanceId = instanceId, Project = "game", UnityVersion = "6000.6.2f1" };
            state.PublishCatalog("catalog-pr213", 1);
            return state;
        }

        static McpCommandContext StatusContext(BridgeRuntimeState state, string operationId)
        {
            return new McpCommandContext(state, "status", "game", "{\"operation_id\":" + JsonUtil.String(operationId) + "}", "{}");
        }

        static string FullArtifactPath(string operationId)
        {
            return Path.Combine(ProjectPaths.ProjectRoot, DynamicCSharpReadonlyResultStore.ArtifactPath(operationId).Replace('/', Path.DirectorySeparatorChar));
        }

        static string Sha256(string value)
        {
            using (var sha256 = SHA256.Create())
            {
                return "sha256:" + BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        sealed class OperationFixture
        {
            internal OperationFixture(string operationId, BridgeRuntimeState state, McpCommandContext context, DynamicCSharpReadonlyOperation operation)
            {
                OperationId = operationId;
                State = state;
                Context = context;
                Operation = operation;
            }

            internal string OperationId { get; private set; }
            internal BridgeRuntimeState State { get; private set; }
            internal McpCommandContext Context { get; private set; }
            internal DynamicCSharpReadonlyOperation Operation { get; private set; }
        }
    }
}
