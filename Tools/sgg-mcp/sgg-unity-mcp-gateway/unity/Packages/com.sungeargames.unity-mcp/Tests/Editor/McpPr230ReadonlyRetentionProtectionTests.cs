using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Commands;

namespace SGG.UnityMCP.Editor.Tests
{
    // Storage/admission fixtures only: no snippet compilation or invocation,
    // consumer acknowledgement, release eligibility or quota-relief proof.
    public sealed class McpPr230ReadonlyRetentionProtectionTests
    {
        const string Code = "return null;";
        const string TerminalData = "{\"retention_fixture\":true}";
        const string TerminalEnvelope = "{\"status\":\"ok\",\"data\":{\"retention_fixture\":true}}";
        const string ProjectionOperationId = "csharp-20261008-120000-01234567";
        const string SensitiveEntry = "private-receipt-id-path-code-caller-secret.json";
        const int FixtureByteLimit = 32768;

        readonly List<string> m_ownedFiles = new List<string>();
        readonly Dictionary<string, FileAttributes> m_originalAttributes = new Dictionary<string, FileAttributes>(StringComparer.Ordinal);
        string m_fixtureId;
        string m_fixtureRoot;
        bool m_fixtureRootOwned;

        [SetUp]
        public void SetUp()
        {
            DynamicCSharpReadonlyResultStore.ResetForTests();
            m_fixtureId = Guid.NewGuid().ToString("N");
            m_fixtureRoot = Path.Combine(ProjectPaths.ProjectRoot, "Library", "SGGUnityMcp", "pr230-fixtures", m_fixtureId);
            Assert.That(Directory.Exists(m_fixtureRoot) || File.Exists(m_fixtureRoot), Is.False, "The unique fixture child must not already exist.");
            m_fixtureRootOwned = true;
            Directory.CreateDirectory(m_fixtureRoot);
        }

        [TearDown]
        public void TearDown()
        {
            DynamicCSharpReadonlyResultStore.ResetForTests();
            Exception cleanupFailure = null;
            for (var index = m_ownedFiles.Count - 1; index >= 0; index--)
            {
                try
                {
                    RestoreOwnedAttributes(m_ownedFiles[index]);
                    if (File.Exists(m_ownedFiles[index])) File.Delete(m_ownedFiles[index]);
                }
                catch (Exception exception)
                {
                    if (cleanupFailure == null) cleanupFailure = exception;
                }
            }
            try
            {
                // Recursive cleanup is confined to this newly owned GUID child,
                // never ArtifactRoot, its parent, or an unrelated receipt.
                if (m_fixtureRootOwned && Directory.Exists(m_fixtureRoot)) Directory.Delete(m_fixtureRoot, true);
            }
            catch (Exception exception)
            {
                if (cleanupFailure == null) cleanupFailure = exception;
            }
            m_ownedFiles.Clear();
            m_originalAttributes.Clear();
            m_fixtureRootOwned = false;
            Assert.That(cleanupFailure, Is.Null, "Fixture-owned storage could not be cleaned up.");
        }

        [TestCase(0, true, "")]
        [TestCase(63, true, "")]
        [TestCase(64, true, "artifact_capacity_exhausted")]
        [TestCase(65, true, "artifact_capacity_exhausted")]
        [TestCase(256, true, "artifact_capacity_exhausted")]
        [TestCase(257, false, "artifact_capacity_unavailable")]
        public void IsolatedMatchingFilesRespectCapacityAndScanBoundWithoutMutation(int count, bool complete, string reason)
        {
            for (var index = 0; index < count; index++)
            {
                WriteOwnedFile(Path.Combine(m_fixtureRoot, index.ToString("D3", CultureInfo.InvariantCulture) + ".json"), new byte[] { 0x7b, 0x7d });
            }
            var before = CaptureOwnedFiles();

            var observation = DynamicCSharpReadonlyResultStore.ObserveArtifactCapacity(() => Directory.EnumerateFiles(m_fixtureRoot, "*.json", SearchOption.TopDirectoryOnly));

            AssertObservation(observation, count, complete, reason);
            AssertOwnedFilesUnchanged(before);
        }

        [Test]
        public void MatchingFilesAreCountedWithoutAgeContentOrAttributeClassification()
        {
            var purportedTerminal = Path.Combine(m_fixtureRoot, "untrusted-terminal.json");
            var malformed = Path.Combine(m_fixtureRoot, "malformed.json");
            var empty = Path.Combine(m_fixtureRoot, "empty.json");
            var aged = Path.Combine(m_fixtureRoot, "aged.json");
            WriteOwnedFile(purportedTerminal, Encoding.UTF8.GetBytes("{\"terminal\":true,\"acknowledged\":true,\"automatic_release_eligible_count\":99}"));
            WriteOwnedFile(malformed, new byte[] { 0xff });
            WriteOwnedFile(empty, new byte[0]);
            WriteOwnedFile(aged, new byte[] { 0x7b, 0x7d });
            WriteOwnedFile(Path.Combine(m_fixtureRoot, "nonmatching.txt"), new byte[] { 0xff });
            var nested = Path.Combine(m_fixtureRoot, "nested");
            Directory.CreateDirectory(nested);
            WriteOwnedFile(Path.Combine(nested, "not-top-level.json"), new byte[] { 0xff });
            AgeOwnedFile(malformed);
            AgeOwnedFile(aged);
            try
            {
                MakeOwnedReadOnly(malformed);
                var before = CaptureOwnedFiles();

                var observation = DynamicCSharpReadonlyResultStore.ObserveArtifactCapacity(() => Directory.EnumerateFiles(m_fixtureRoot, "*.json", SearchOption.TopDirectoryOnly));

                AssertObservation(observation, 4, true, string.Empty);
                AssertOwnedFilesUnchanged(before);
            }
            finally
            {
                RestoreOwnedAttributes(malformed);
            }
        }

        [Test]
        public void InfiniteEnumerationStopsAt257AndDisposesTheEnumerator()
        {
            var entries = new ProbeEnumerable(0, infinite: true);
            var factoryCalls = 0;

            var observation = DynamicCSharpReadonlyResultStore.ObserveArtifactCapacity(() =>
            {
                factoryCalls++;
                return entries;
            });

            AssertObservation(observation, 257, false, "artifact_capacity_unavailable");
            Assert.That(factoryCalls, Is.EqualTo(1));
            Assert.That(entries.GetEnumeratorCalls, Is.EqualTo(1));
            Assert.That(entries.MoveNextCalls, Is.EqualTo(257));
            Assert.That(entries.CurrentCalls, Is.EqualTo(257));
            Assert.That(entries.DisposeCalls, Is.EqualTo(1));
        }

        [Test]
        public void EnumerationFactoryFailureReportsZeroObservedNotAnEmptyStore()
        {
            var factoryCalls = 0;

            var observation = DynamicCSharpReadonlyResultStore.ObserveArtifactCapacity(() =>
            {
                factoryCalls++;
                throw new InvalidOperationException(SensitiveEntry);
            });

            AssertObservation(observation, 0, false, "artifact_status_unavailable");
            Assert.That(factoryCalls, Is.EqualTo(1));
        }

        [Test]
        public void GetEnumeratorFailureReportsZeroObservedWithoutInventingCompletion()
        {
            var entries = new ProbeEnumerable(0, throwOnGetEnumerator: true);

            var observation = DynamicCSharpReadonlyResultStore.ObserveArtifactCapacity(() => entries);

            AssertObservation(observation, 0, false, "artifact_status_unavailable");
            Assert.That(entries.GetEnumeratorCalls, Is.EqualTo(1));
            Assert.That(entries.MoveNextCalls, Is.Zero);
            Assert.That(entries.CurrentCalls, Is.Zero);
            Assert.That(entries.DisposeCalls, Is.Zero);
        }

        [TestCase(0)]
        [TestCase(3)]
        public void DeferredMoveNextFailureRetainsTheObservedLowerBoundAndDisposes(int successfulItems)
        {
            var entries = new ProbeEnumerable(successfulItems, throwAfter: successfulItems);

            var observation = DynamicCSharpReadonlyResultStore.ObserveArtifactCapacity(() => entries);

            AssertObservation(observation, successfulItems, false, "artifact_status_unavailable");
            Assert.That(entries.GetEnumeratorCalls, Is.EqualTo(1));
            Assert.That(entries.MoveNextCalls, Is.EqualTo(successfulItems + 1));
            Assert.That(entries.CurrentCalls, Is.EqualTo(successfulItems));
            Assert.That(entries.DisposeCalls, Is.EqualTo(1));
        }

        [TestCase(64, true, "artifact_capacity_exhausted")]
        [TestCase(257, false, "artifact_capacity_unavailable")]
        [TestCase(0, false, "artifact_status_unavailable")]
        [TestCase(3, false, "artifact_status_unavailable")]
        public void FaultProjectionPreservesTheErrorContractAndOnlyAllowlistedCountFacts(int count, bool complete, string reason)
        {
            var entries = new ProbeEnumerable(count, throwAfter: reason == "artifact_status_unavailable" ? count : -1);
            var observation = DynamicCSharpReadonlyResultStore.ObserveArtifactCapacity(() => entries);
            AssertObservation(observation, count, complete, reason);

            var error = FormatStoreError(ProjectionOperationId, reason, observation);

            AssertCapacityError(error, ProjectionOperationId, observation);
            Assert.That(error.ErrorDetailsJson, Does.Not.Contain(SensitiveEntry));
            Assert.That(error.ErrorDetailsJson, Does.Not.Contain("\"artifact_path\""));
            Assert.That(error.ErrorDetailsJson, Does.Not.Contain("\"caller_args_hash\""));
            Assert.That(error.ErrorDetailsJson, Does.Not.Contain("\"code\""));
        }

        [TestCase(0)]
        [TestCase(64)]
        [TestCase(257)]
        public void DisposalFailureNeverClaimsCompletedEnumeration(int count)
        {
            var entries = new ProbeEnumerable(count, throwOnDispose: true);

            var observation = DynamicCSharpReadonlyResultStore.ObserveArtifactCapacity(() => entries);

            AssertObservation(observation, count, false, "artifact_status_unavailable");
            Assert.That(entries.MoveNextCalls, Is.EqualTo(count == 257 ? 257 : count + 1));
            Assert.That(entries.CurrentCalls, Is.EqualTo(count));
            Assert.That(entries.DisposeCalls, Is.EqualTo(1));
            var error = FormatStoreError(ProjectionOperationId, observation.FailureReason, observation);
            AssertCapacityError(error, ProjectionOperationId, observation);
            Assert.That(error.ErrorDetailsJson, Does.Not.Contain(SensitiveEntry));
        }

        [Test]
        public void FaultProjectionWithoutAnObservationOmitsTheRawJsonProperty()
        {
            var error = FormatStoreError(ProjectionOperationId, "artifact_status_unavailable", null);

            AssertStoreError(error, ProjectionOperationId, "artifact_status_unavailable", null);
            // JsonUtility can create default nested PODs for absent properties;
            // absence is a JSON contract, not an assertion about a CLR null.
            Assert.That(error.ErrorDetailsJson, Does.Not.Contain("\"capacity_observation\""));
        }

        [TestCase("active_running")]
        [TestCase("terminal_response_completed")]
        [TestCase("disk_only_terminal")]
        public void RealAdmissionAtCapacityRetainsAgedReceiptsAndUntrustedReadOnlyFiles(string receiptKind)
        {
            var baseline = RequireRealAdmissionRoom();
            var fixture = BeginOwnOperation();
            var terminal = receiptKind != "active_running";
            if (terminal)
            {
                PublishOwnTerminal(fixture);
            }
            else
            {
                Assert.That(fixture.Operation.TryStartPhase("reference_resolution", out var phaseError), Is.True, "The owned running phase must publish successfully.");
                Assert.That(phaseError, Is.Null);
            }
            AgeOwnedFile(fixture.Path);
            if (receiptKind == "disk_only_terminal") DynamicCSharpReadonlyResultStore.ResetForTests();
            try
            {
                FillOwnedAgedCapacityFiles();
                var count = ObserveRealRoot();
                AssertObservation(count, baseline.ObservedMatchingFiles + 65, true, "artifact_capacity_exhausted");
                var rejected = NewAttempt(fixture.State);
                var before = CaptureOwnedFiles();
                var namesBefore = RealRootNamesDigest();

                var began = DynamicCSharpReadonlyResultStore.TryBegin(rejected.Context, Code, out var operation, out var error);

                Assert.That(began, Is.False);
                Assert.That(operation, Is.Null);
                AssertCapacityError(error, rejected.OperationId, count);
                Assert.That(File.Exists(rejected.Path), Is.False, "Rejected admission must not create a receipt.");
                Assert.That(RealRootNamesDigest(), Is.EqualTo(namesBefore), "Admission must not change root entry names.");
                AssertOwnedFilesUnchanged(before);

                // Exactly one explicit status read of our receipt, never an
                // admission classification/repair or invocation of Code.
                var status = DynamicCSharpReadonlyResultStore.Status(StatusContext(fixture));

                AssertOwnStatus(status, fixture, terminal);
                if (!terminal) Assert.That(status.DataJson, Does.Contain("\"reference_resolution\":{\"state\":\"running\""));
                Assert.That(RealRootNamesDigest(), Is.EqualTo(namesBefore));
                AssertOwnedFilesUnchanged(before);
                AssertOwnReceiptIntegrity(fixture, terminal);
            }
            finally
            {
                RestoreAllOwnedAttributes();
            }
        }

        [Test]
        public void AdmissionDoesNotRepairMissingTerminalDiskButOneExactStatusReadDoes()
        {
            var baseline = RequireRealAdmissionRoom();
            var fixture = BeginOwnOperation();
            PublishOwnTerminal(fixture);
            AssertOwnedByteBudget();
            var originalReceipt = File.ReadAllBytes(fixture.Path);
            File.Delete(fixture.Path);
            Assert.That(File.Exists(fixture.Path), Is.False);
            RequireRealAdmissionRoom();
            var next = NewAttempt(fixture.State);
            var namesBefore = RealRootNamesDigest();

            var began = DynamicCSharpReadonlyResultStore.TryBegin(next.Context, Code, out var operation, out var error);

            Assert.That(began, Is.True, "A separate owned admission must have room; no foreign receipts may be removed.");
            Assert.That(error, Is.Null);
            Assert.That(operation, Is.Not.Null);
            Assert.That(File.Exists(next.Path), Is.True);
            AssertOwnedByteBudget();
            Assert.That(File.Exists(fixture.Path), Is.False, "Admission must not implicitly repair a missing cached terminal receipt.");
            Assert.That(RealRootNamesDigest(next.Path), Is.EqualTo(namesBefore));

            var status = DynamicCSharpReadonlyResultStore.Status(StatusContext(fixture));

            AssertOwnStatus(status, fixture, true);
            Assert.That(File.Exists(fixture.Path), Is.True);
            Assert.That(File.ReadAllBytes(fixture.Path), Is.EqualTo(originalReceipt), "Explicit status repair must preserve the owned terminal receipt.");
            Assert.That(RealRootNamesDigest(next.Path, fixture.Path), Is.EqualTo(namesBefore));
            var after = ObserveRealRoot();
            Assert.That(after.EnumerationComplete, Is.True);
            Assert.That(after.ObservedMatchingFiles, Is.EqualTo(baseline.ObservedMatchingFiles + 2));
            // Repair may exceed the admission threshold; it is not a universal
            // file-count ceiling and is not an acknowledgement/release action.
            AssertOwnedByteBudget();
            AssertOwnReceiptIntegrity(fixture, true);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DuplicateOwnOperationKeepsItsGateAndOmitsCapacityObservation(bool diskOnly)
        {
            RequireRealAdmissionRoom();
            var fixture = BeginOwnOperation();
            if (diskOnly) DynamicCSharpReadonlyResultStore.ResetForTests();
            try
            {
                FillOwnedAgedCapacityFiles();
                var before = CaptureOwnedFiles();
                var namesBefore = RealRootNamesDigest();

                var began = DynamicCSharpReadonlyResultStore.TryBegin(fixture.Context, Code, out var operation, out var error);

                Assert.That(began, Is.False);
                Assert.That(operation, Is.Null);
                AssertStoreError(error, fixture.OperationId, "operation_exists", null);
                Assert.That(error.ErrorDetailsJson, Does.Not.Contain("\"capacity_observation\""));
                Assert.That(RealRootNamesDigest(), Is.EqualTo(namesBefore));
                AssertOwnedFilesUnchanged(before);
            }
            finally
            {
                RestoreAllOwnedAttributes();
            }
        }

        [Test]
        public void InvalidCallerBindingFailsBeforeStorageWithoutCapacityObservation()
        {
            var attempt = NewAttempt(State());
            var invalidArgs = attempt.Context.ArgsJson.Replace(Sha256("caller-args"), "not-sha256");
            var context = new McpCommandContext(attempt.State, "request", "game", invalidArgs, "{}");
            var rootExisted = Directory.Exists(RealArtifactRoot);
            var namesBefore = RealRootNamesDigest();
            var before = CaptureOwnedFiles();

            var began = DynamicCSharpReadonlyResultStore.TryBegin(context, Code, out var operation, out var error);

            Assert.That(began, Is.False);
            Assert.That(operation, Is.Null);
            Assert.That(error, Is.Not.Null);
            Assert.That(error.Status, Is.EqualTo("error"));
            Assert.That(error.ErrorCode, Is.EqualTo("dynamic_csharp_result_binding_invalid"));
            Assert.That(error.ErrorMessage, Is.EqualTo("Gateway dynamic C# read-only operation binding is invalid"));
            Assert.That(error.ErrorDetailsJson, Is.EqualTo("{\"operation_id\":" + JsonUtil.String(attempt.OperationId) + ",\"execution_may_have_started\":false,\"command_replayed\":false}"));
            Assert.That(error.ErrorDetailsJson, Does.Not.Contain("\"capacity_observation\""));
            Assert.That(File.Exists(attempt.Path), Is.False);
            Assert.That(Directory.Exists(RealArtifactRoot), Is.EqualTo(rootExisted));
            Assert.That(RealRootNamesDigest(), Is.EqualTo(namesBefore));
            AssertOwnedFilesUnchanged(before);
        }

        static string RealArtifactRoot
        {
            get { return Path.Combine(ProjectPaths.ProjectRoot, DynamicCSharpReadonlyResultStore.ArtifactRoot.Replace('/', Path.DirectorySeparatorChar)); }
        }

        static DynamicCSharpReadonlyResultStore.ArtifactCapacityObservation ObserveRealRoot()
        {
            return DynamicCSharpReadonlyResultStore.ObserveArtifactCapacity(() => Directory.Exists(RealArtifactRoot)
                ? Directory.EnumerateFiles(RealArtifactRoot, "*.json", SearchOption.TopDirectoryOnly)
                : (IEnumerable<string>)new string[0]);
        }

        static DynamicCSharpReadonlyResultStore.ArtifactCapacityObservation RequireRealAdmissionRoom()
        {
            var observation = ObserveRealRoot();
            Assert.That(observation.EnumerationComplete, Is.True, "BLOCKED: genuine admission requires a completely enumerated baseline; no unrelated receipts were changed.");
            Assert.That(observation.FailureReason, Is.Empty, "BLOCKED: genuine admission requires fewer than 64 existing matching entries; no quota relief is authorized.");
            Assert.That(observation.ObservedMatchingFiles, Is.LessThan(64));
            return observation;
        }

        OperationFixture NewAttempt(BridgeRuntimeState state)
        {
            var operationId = "csharp-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var path = Path.Combine(ProjectPaths.ProjectRoot, DynamicCSharpReadonlyResultStore.ArtifactPath(operationId).Replace('/', Path.DirectorySeparatorChar));
            TrackNewOwnedFile(path);
            var args = "{\"code\":" + JsonUtil.String(Code)
                + ",\"_dynamic_csharp_operation_id\":" + JsonUtil.String(operationId)
                + ",\"_dynamic_csharp_caller_args_hash\":" + JsonUtil.String(Sha256("caller-args"))
                + ",\"_dynamic_csharp_queue_wait_ms\":12}";
            return new OperationFixture(operationId, path, state, new McpCommandContext(state, "request", "game", args, "{}"));
        }

        OperationFixture BeginOwnOperation()
        {
            var fixture = NewAttempt(State());
            var began = DynamicCSharpReadonlyResultStore.TryBegin(fixture.Context, Code, out var operation, out var error);
            Assert.That(began, Is.True, "BLOCKED: the owned receipt could not be admitted; do not clear unrelated receipts or retry execution.");
            Assert.That(error, Is.Null);
            Assert.That(operation, Is.Not.Null);
            fixture.Operation = operation;
            Assert.That(File.Exists(fixture.Path), Is.True);
            AssertOwnedByteBudget();
            return fixture;
        }

        static BridgeRuntimeState State()
        {
            var state = new BridgeRuntimeState { InstanceId = "game-current", Project = "game", UnityVersion = "6000.6.2f1" };
            state.PublishCatalog("catalog-pr230", 1);
            return state;
        }

        static McpCommandContext StatusContext(OperationFixture fixture)
        {
            return new McpCommandContext(fixture.State, "status", "game", "{\"operation_id\":" + JsonUtil.String(fixture.OperationId) + "}", "{}");
        }

        static void PublishOwnTerminal(OperationFixture fixture)
        {
            // Phase evidence is synthetic, as in PR213; these calls do not
            // compile or execute the snippet or perform a native phase body.
            foreach (var phase in new[] { "reference_resolution", "compilation", "main_thread_execution", "result_serialization" })
            {
                Assert.That(fixture.Operation.TryStartPhase(phase, out var startError), Is.True, "The owned phase start must publish successfully.");
                Assert.That(startError, Is.Null);
                Assert.That(fixture.Operation.TryFinishPhase(phase, 1, true, out var finishError), Is.True, "The owned phase completion must publish successfully.");
                Assert.That(finishError, Is.Null);
            }
            Assert.That(fixture.Operation.PublishTerminal(CommandExecutionResult.Ok(TerminalData)).Status, Is.EqualTo("ok"));
            DynamicCSharpReadonlyResultStore.MarkResponseWrite(fixture.Context.ArgsJson, "completed", 7);
        }

        void FillOwnedAgedCapacityFiles()
        {
            for (var index = 0; index < 64; index++)
            {
                var path = Path.Combine(RealArtifactRoot, "pr230-" + m_fixtureId + "-" + index.ToString("D2", CultureInfo.InvariantCulture) + ".json");
                WriteOwnedFile(path, index == 0 ? new byte[] { 0xff } : new byte[] { 0x7b, 0x7d });
                AgeOwnedFile(path);
                if (index == 0) MakeOwnedReadOnly(path);
            }
            AssertOwnedByteBudget();
        }

        static void AssertOwnStatus(CommandExecutionResult status, OperationFixture fixture, bool terminal)
        {
            Assert.That(status.Status, Is.EqualTo("ok"), "The single explicit status read must validate the original owned receipt.");
            Assert.That(status.DataJson, Does.Contain("\"operation_id\":" + JsonUtil.String(fixture.OperationId)));
            Assert.That(status.DataJson, Does.Contain("\"artifact_path\":" + JsonUtil.String(DynamicCSharpReadonlyResultStore.ArtifactPath(fixture.OperationId))));
            Assert.That(status.DataJson, Does.Contain("\"caller_args_hash\":" + JsonUtil.String(Sha256("caller-args"))));
            Assert.That(status.DataJson, Does.Contain("\"snippet_hash\":" + JsonUtil.String(Sha256(Code))));
            Assert.That(status.DataJson, Does.Contain("\"owner_binding\":{\"project\":\"game\",\"instance_id\":\"game-current\""));
            Assert.That(status.DataJson, Does.Contain("\"commands_hash\":\"catalog-pr230\""));
            Assert.That(status.DataJson, Does.Contain("\"catalog_generation\":1"));
            Assert.That(status.DataJson, Does.Contain("\"project_queue_admission\":{\"state\":\"completed\",\"duration_ms\":12}"));
            Assert.That(status.DataJson, Does.Contain("\"command_replayed\":false"));
            Assert.That(status.DataJson, Does.Contain(terminal ? "\"terminal\":true" : "\"terminal\":false"));
            Assert.That(status.DataJson, Does.Contain(terminal ? "\"result_available\":true" : "\"result_available\":false"));
            Assert.That(status.DataJson, Does.Contain(terminal ? "\"status\":\"completed\"" : "\"status\":\"running\""));
            Assert.That(status.DataJson, Does.Contain("\"command_result\":" + (terminal ? TerminalEnvelope : "null")));
            if (terminal) Assert.That(status.DataJson, Does.Contain("\"bridge_response_write\":{\"state\":\"completed\",\"duration_ms\":7}"));
        }

        static void AssertOwnReceiptIntegrity(OperationFixture fixture, bool terminal)
        {
            var receiptJson = File.ReadAllText(fixture.Path, new UTF8Encoding(false, true));
            Assert.That(receiptJson, Does.Contain("\"receipt_hash\":" + JsonUtil.String(fixture.Operation.Receipt.receipt_hash)));
            if (terminal) Assert.That(receiptJson, Does.Contain("\"command_result_hash\":" + JsonUtil.String(Sha256(TerminalEnvelope))));
        }

        static void AssertObservation(DynamicCSharpReadonlyResultStore.ArtifactCapacityObservation observation, int count, bool complete, string reason)
        {
            Assert.That(observation, Is.Not.Null);
            Assert.That(observation.ObservedMatchingFiles, Is.EqualTo(count));
            Assert.That(observation.EnumerationComplete, Is.EqualTo(complete));
            Assert.That(observation.FailureReason, Is.EqualTo(reason));
        }

        static CommandExecutionResult FormatStoreError(string operationId, string reason, DynamicCSharpReadonlyResultStore.ArtifactCapacityObservation observation)
        {
            // Only the pure error formatter is invoked by reflection, never
            // admission, owner/path validation, publishing or a snippet body.
            var method = typeof(DynamicCSharpReadonlyResultStore).GetMethod("StoreError", BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(string), typeof(string), typeof(bool), typeof(DynamicCSharpReadonlyResultStore.ArtifactCapacityObservation) }, null);
            Assert.That(method, Is.Not.Null);
            return (CommandExecutionResult)method.Invoke(null, new object[] { operationId, reason, false, observation });
        }

        static void AssertCapacityError(CommandExecutionResult error, string operationId, DynamicCSharpReadonlyResultStore.ArtifactCapacityObservation observation)
        {
            AssertStoreError(error, operationId, observation.FailureReason, observation);
        }

        static void AssertStoreError(CommandExecutionResult error, string operationId, string reason, DynamicCSharpReadonlyResultStore.ArtifactCapacityObservation observation)
        {
            Assert.That(error, Is.Not.Null);
            Assert.That(error.Status, Is.EqualTo("error"));
            Assert.That(error.ErrorCode, Is.EqualTo("dynamic_csharp_result_unavailable"));
            Assert.That(error.ErrorMessage, Is.EqualTo("Dynamic C# read-only result store is unavailable"));
            var expected = "{\"operation_id\":" + JsonUtil.String(operationId)
                + ",\"reason\":" + JsonUtil.String(reason)
                + ",\"execution_may_have_started\":false,\"command_replayed\":false";
            if (observation != null)
            {
                expected += ",\"capacity_observation\":{\"observation_source\":\"matching_file_enumeration\",\"observed_matching_files\":"
                    + observation.ObservedMatchingFiles.ToString(CultureInfo.InvariantCulture)
                    + ",\"enumeration_complete\":" + (observation.EnumerationComplete ? "true" : "false")
                    + ",\"atomic_snapshot\":false,\"max_retained_artifacts\":64,\"max_matching_files_scan\":256"
                    + ",\"automatic_retention_cleanup_enabled\":false,\"automatic_release_eligible_count\":0"
                    + ",\"automatic_release_eligibility_source\":\"disabled_by_contract\""
                    + ",\"receipt_state_classification\":\"not_performed\",\"receipt_age_classification\":\"not_performed\"}";
            }
            // Exact allowlist also catches default/missing false-valued fields,
            // untrusted names, exception messages and additional secret fields.
            Assert.That(error.ErrorDetailsJson, Is.EqualTo(expected + "}"));
        }

        void TrackNewOwnedFile(string path)
        {
            Assert.That(File.Exists(path) || Directory.Exists(path), Is.False, "A new fixture-owned path must not replace an existing entry.");
            Assert.That(m_ownedFiles.Contains(path), Is.False);
            m_ownedFiles.Add(path);
        }

        void WriteOwnedFile(string path, byte[] bytes)
        {
            TrackNewOwnedFile(path);
            File.WriteAllBytes(path, bytes);
        }

        void AgeOwnedFile(string path)
        {
            Assert.That(m_ownedFiles.Contains(path), Is.True, "Only an individually tracked fixture file may be aged.");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-48));
        }

        void MakeOwnedReadOnly(string path)
        {
            Assert.That(m_ownedFiles.Contains(path), Is.True);
            var attributes = File.GetAttributes(path);
            m_originalAttributes.Add(path, attributes);
            File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
        }

        void RestoreOwnedAttributes(string path)
        {
            if (m_originalAttributes.TryGetValue(path, out var attributes) && File.Exists(path)) File.SetAttributes(path, attributes);
        }

        void RestoreAllOwnedAttributes()
        {
            foreach (var path in m_ownedFiles) RestoreOwnedAttributes(path);
        }

        void AssertOwnedByteBudget()
        {
            long total = 0;
            foreach (var path in m_ownedFiles)
            {
                if (File.Exists(path)) total += new FileInfo(path).Length;
            }
            Assert.That(total, Is.LessThanOrEqualTo(FixtureByteLimit), "Fixture-owned files must stay within 32 KiB; foreign contents are never read.");
        }

        List<OwnedFileSnapshot> CaptureOwnedFiles()
        {
            AssertOwnedByteBudget();
            var snapshots = new List<OwnedFileSnapshot>();
            foreach (var path in m_ownedFiles)
            {
                var exists = File.Exists(path);
                snapshots.Add(new OwnedFileSnapshot
                {
                    Path = path,
                    Exists = exists,
                    Hash = exists ? Sha256(File.ReadAllBytes(path)) : string.Empty,
                    Attributes = exists ? File.GetAttributes(path) : default(FileAttributes),
                    ModifiedUtc = exists ? File.GetLastWriteTimeUtc(path) : default(DateTime)
                });
            }
            return snapshots;
        }

        void AssertOwnedFilesUnchanged(List<OwnedFileSnapshot> before)
        {
            AssertOwnedByteBudget();
            foreach (var snapshot in before)
            {
                Assert.That(File.Exists(snapshot.Path), Is.EqualTo(snapshot.Exists), "Owned file existence changed.");
                if (!snapshot.Exists) continue;
                Assert.That(Sha256(File.ReadAllBytes(snapshot.Path)), Is.EqualTo(snapshot.Hash), "Owned file bytes changed.");
                Assert.That(File.GetAttributes(snapshot.Path), Is.EqualTo(snapshot.Attributes), "Owned file attributes changed.");
                Assert.That(File.GetLastWriteTimeUtc(snapshot.Path), Is.EqualTo(snapshot.ModifiedUtc), "Owned file modification time changed.");
            }
        }

        string RealRootNamesDigest(params string[] excludedOwnedPaths)
        {
            var excludedNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in excludedOwnedPaths)
            {
                Assert.That(m_ownedFiles.Contains(path), Is.True);
                excludedNames.Add(Path.GetFileName(path));
            }
            var names = new List<string>();
            var observed = 0;
            if (Directory.Exists(RealArtifactRoot))
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(RealArtifactRoot, "*", SearchOption.TopDirectoryOnly))
                {
                    observed++;
                    Assert.That(observed, Is.LessThanOrEqualTo(256), "BLOCKED: the non-disclosive root-name snapshot exceeds its bound.");
                    var name = Path.GetFileName(path);
                    if (!excludedNames.Contains(name)) names.Add(name);
                }
            }
            names.Sort(StringComparer.Ordinal);
            var value = new StringBuilder();
            foreach (var name in names) value.Append(name.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(name).Append('\n');
            return Sha256(value.ToString());
        }

        static string Sha256(string value)
        {
            return Sha256(Encoding.UTF8.GetBytes(value));
        }

        static string Sha256(byte[] bytes)
        {
            using (var sha256 = SHA256.Create())
            {
                return "sha256:" + BitConverter.ToString(sha256.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        sealed class OwnedFileSnapshot
        {
            internal string Path;
            internal bool Exists;
            internal string Hash;
            internal FileAttributes Attributes;
            internal DateTime ModifiedUtc;
        }

        sealed class OperationFixture
        {
            internal OperationFixture(string operationId, string path, BridgeRuntimeState state, McpCommandContext context)
            {
                OperationId = operationId;
                Path = path;
                State = state;
                Context = context;
            }

            internal readonly string OperationId;
            internal readonly string Path;
            internal readonly BridgeRuntimeState State;
            internal readonly McpCommandContext Context;
            internal DynamicCSharpReadonlyOperation Operation;
        }

        sealed class ProbeEnumerable : IEnumerable<string>
        {
            readonly int m_itemCount;
            readonly int m_throwAfter;
            readonly bool m_infinite;
            readonly bool m_throwOnGetEnumerator;
            readonly bool m_throwOnDispose;
            internal int GetEnumeratorCalls;
            internal int MoveNextCalls;
            internal int CurrentCalls;
            internal int DisposeCalls;

            internal ProbeEnumerable(int itemCount, int throwAfter = -1, bool infinite = false, bool throwOnGetEnumerator = false, bool throwOnDispose = false)
            {
                m_itemCount = itemCount;
                m_throwAfter = throwAfter;
                m_infinite = infinite;
                m_throwOnGetEnumerator = throwOnGetEnumerator;
                m_throwOnDispose = throwOnDispose;
            }

            public IEnumerator<string> GetEnumerator()
            {
                GetEnumeratorCalls++;
                if (m_throwOnGetEnumerator) throw new InvalidOperationException(SensitiveEntry);
                return new Enumerator(this);
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }

            sealed class Enumerator : IEnumerator<string>
            {
                readonly ProbeEnumerable m_owner;
                int m_yielded;

                internal Enumerator(ProbeEnumerable owner)
                {
                    m_owner = owner;
                }

                public string Current
                {
                    get
                    {
                        m_owner.CurrentCalls++;
                        return SensitiveEntry;
                    }
                }

                object IEnumerator.Current { get { return Current; } }

                public bool MoveNext()
                {
                    m_owner.MoveNextCalls++;
                    if (m_owner.m_throwAfter >= 0 && m_yielded >= m_owner.m_throwAfter) throw new InvalidOperationException(SensitiveEntry);
                    if (!m_owner.m_infinite && m_yielded >= m_owner.m_itemCount) return false;
                    m_yielded++;
                    return true;
                }

                public void Dispose()
                {
                    m_owner.DisposeCalls++;
                    if (m_owner.m_throwOnDispose) throw new InvalidOperationException(SensitiveEntry);
                }

                public void Reset()
                {
                    throw new NotSupportedException();
                }
            }
        }
    }
}
