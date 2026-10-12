using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Commands;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Tests
{
    // Source-local synthetic evidence only: these tests do not reproduce a
    // reported receipt, execute TestRunner, or prove historical callback loss.
    public sealed class McpPr229NonPassingEvidenceTests
    {
        const int EntryLimit = 64;
        const int NodeLimit = 20000;
        const int DepthLimit = 64;
        const int AdditionByteLimit = 48000;
        const int ArtifactByteLimit = 200000;
        const string Hash = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string DefaultPrefix = "{\"base\":true";

        [TestCase("Failed:Assertion")]
        [TestCase("Failure:Assertion")]
        [TestCase("Error:Exception")]
        [TestCase("Inconclusive:NoResult")]
        [TestCase("Skipped:Explicit")]
        [TestCase("Ignored:Platform")]
        public void CapturePreservesExactNonPassingRawStatus(string rawStatus)
        {
            var evidence = new TestRunNonPassingEvidence();
            var result = Leaf("Fixture.Case", "Passed");
            result.ResultState = new AdapterResultState(rawStatus);
            result.Outcome = "Error:LowerPriority";
            evidence.Capture(result);

            var document = Read(evidence);

            AssertEvidence(document, 1, "partial", false);
            Assert.That(document.non_passing_tests[0].raw_status, Is.EqualTo(rawStatus));
            Assert.That(document.non_passing_tests[0].full_name, Is.EqualTo("Fixture.Case"));
            Assert.That(document.non_passing_tests[0].unique_name, Is.EqualTo("Fixture.Case"));

            // ResultState is the adapter's whole state object. Only an absent
            // value permits TestStatus, then Outcome, as lower-priority sources.
            result.ResultState = null;
            result.TestStatus = rawStatus;
            evidence.CaptureTree(Suite("Root", new object[] { result }));
            AssertEvidence(Read(evidence), 1, "complete", false);
            Assert.That(Read(evidence).non_passing_tests[0].raw_status, Is.EqualTo(rawStatus));
            result.TestStatus = null;
            result.Outcome = rawStatus;
            evidence.CaptureTree(Suite("Root", new object[] { result }));
            AssertEvidence(Read(evidence), 1, "complete", false);
            Assert.That(Read(evidence).non_passing_tests[0].raw_status, Is.EqualTo(rawStatus));
        }

        [Test]
        public void CallbackCaptureIsPartialAndNeverVisitsAChildSubtree()
        {
            var evidence = new TestRunNonPassingEvidence();
            evidence.Capture(Leaf("Fixture.Callback", "Inconclusive"));
            AssertEvidence(Read(evidence), 1, "partial", false);
            var child = new ThrowingTestResult();
            var malformedLeaf = Leaf("Fixture.NotAContainer", "Failed");
            malformedLeaf.Children = new object[] { child };

            evidence.Capture(malformedLeaf);

            var document = Read(evidence);
            AssertEvidence(document, 1, "partial", false);
            Assert.That(document.non_passing_tests[0].full_name, Is.EqualTo("Fixture.Callback"));
            Assert.That(child.TestGetterCalls, Is.Zero);
            evidence.Capture(Suite("Callback.Suite", new object[] { child }, "Failed"));
            AssertEvidence(Read(evidence), 1, "partial", false);
            Assert.That(child.TestGetterCalls, Is.Zero);
        }

        [Test]
        public void FreshTerminalTraversalReplacesCallbacksAndCertifiesOnlyAnObservedValidTree()
        {
            var evidence = new TestRunNonPassingEvidence();
            evidence.Capture(Leaf("Callback.Only", "Ignored", "Callback.Assembly"));
            evidence.Capture(Leaf("Fixture.Changed", "Failed", "Callback.Assembly"));
            var assemblyNode = Leaf("Actual.Assembly.Node", "Failed", "Observed.Assembly");
            ((TestMetadata)assemblyNode.Test).IsTestAssembly = true;
            var root = Suite("Observed.Assembly", new object[]
            {
                assemblyNode,
                Leaf("Fixture.Changed", "Passed", string.Empty),
                Leaf("Fixture.Inconclusive", "Inconclusive", string.Empty),
                Suite("Fixture.FailedSuite", new object[] { Leaf("Fixture.Ignored", "Ignored", string.Empty) }, "Failed", string.Empty),
                Suite("Fixture.EmptySuite", new object[0], "Passed", string.Empty),
            }, "Failed", "Observed.Assembly", true);

            evidence.CaptureTree(root);

            var document = Read(evidence);
            AssertEvidence(document, 2, "complete", false);
            foreach (var entry in document.non_passing_tests)
            {
                Assert.That(entry.assembly, Is.EqualTo("Observed.Assembly"));
                Assert.That(entry.full_name, Is.Not.EqualTo("Callback.Only"));
                Assert.That(entry.full_name, Is.Not.EqualTo("Fixture.Changed"));
                Assert.That(entry.full_name, Is.Not.EqualTo("Fixture.FailedSuite"));
                Assert.That(entry.full_name, Is.Not.EqualTo("Observed.Assembly"));
                Assert.That(entry.full_name, Is.Not.EqualTo("Actual.Assembly.Node"));
            }

            evidence.Capture(Leaf("Callback.BeforePartialTree", "Failed", "Callback.Assembly"));
            evidence.CaptureTree(Suite("Partial.Root", new object[] { new ThrowingTestResult(), Leaf("Fresh.Terminal", "Ignored") }));
            var partial = Read(evidence);
            AssertEvidence(partial, 1, "partial", false);
            Assert.That(partial.non_passing_tests[0].full_name, Is.EqualTo("Fresh.Terminal"));
            Assert.That(partial.non_passing_tests[0].assembly, Is.EqualTo("Fixture.Editor"));

            evidence.CaptureTree(Suite("Observed.Empty", new object[0]));
            AssertEvidence(Read(evidence), 0, "complete", false);
            evidence.CaptureTree(Suite("Observed.Passed", new object[] { Leaf("Fixture.Passed", "Passed:NotFailed") }));
            AssertEvidence(Read(evidence), 0, "complete", false);
            foreach (var unknown in new[] { "Unknown", "NotFailed", "Failedish", "Inconclusiveish" })
            {
                evidence.CaptureTree(Suite("Observed.Unknown", new object[] { Leaf("Fixture.Unknown", unknown) }));
                AssertEvidence(Read(evidence), 0, "partial", false);
            }
        }

        [Test]
        public void TestMetadataIsPreferredWithoutInventingAssemblyFromTheRequestedFilter()
        {
            var leaf = Leaf("Observed.Exact", "Ignored", "Observed.Assembly");
            leaf.Name = "Flat.Name";
            leaf.FullName = "Flat.Full";
            leaf.UniqueName = "Flat.Unique";
            leaf.AssemblyName = "Requested.Filter.Assembly";
            leaf.IsSuite = true;
            leaf.IsTestAssembly = true;
            var evidence = new TestRunNonPassingEvidence();

            evidence.CaptureTree(Suite("Actual.Root", new object[] { leaf }));

            var document = Read(evidence);
            AssertEvidence(document, 1, "complete", false);
            Assert.That(document.non_passing_tests[0].name, Is.EqualTo("Case"));
            Assert.That(document.non_passing_tests[0].full_name, Is.EqualTo("Observed.Exact"));
            Assert.That(document.non_passing_tests[0].unique_name, Is.EqualTo("Observed.Exact"));
            Assert.That(document.non_passing_tests[0].assembly, Is.EqualTo("Observed.Assembly"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MissingOrNullTestAllowsObservedFlatMetadata(bool nullTest)
        {
            var evidence = new TestRunNonPassingEvidence();
            object result = nullTest
                ? (object)new ResultNode { Test = null, Name = "Flat", FullName = "Flat.Exact", UniqueName = "Flat.Unique", AssemblyName = "Flat.Assembly", TestStatus = "Skipped", ResultState = "Skipped", Children = new object[0] }
                : new FlatResultWithoutTest();

            evidence.CaptureTree(Suite("Root", new object[] { result }));

            var document = Read(evidence);
            AssertEvidence(document, 1, "complete", false);
            Assert.That(document.non_passing_tests[0].full_name, Is.EqualTo("Flat.Exact"));
            Assert.That(document.non_passing_tests[0].unique_name, Is.EqualTo("Flat.Unique"));
            Assert.That(document.non_passing_tests[0].assembly, Is.EqualTo("Flat.Assembly"));
        }

        [Test]
        public void ThrowingTestGetterIsPartialWithoutSilentlyUsingFlatIdentity()
        {
            var evidence = new TestRunNonPassingEvidence();
            var throwing = new ThrowingTestResult();

            Assert.DoesNotThrow(() => evidence.CaptureTree(Suite("Root", new object[] { throwing, Leaf("Valid.Sibling", "Ignored") })));

            var document = Read(evidence);
            AssertEvidence(document, 1, "partial", false);
            Assert.That(document.non_passing_tests[0].full_name, Is.EqualTo("Valid.Sibling"));
            Assert.That(throwing.TestGetterCalls, Is.GreaterThan(0));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void OneObservedExactIdentityIsSufficientWithoutInventingTheOther(bool missingFullName)
        {
            const string identity = "Fixture.Case(\"a\\b\")";
            var leaf = Leaf(identity, "Inconclusive");
            var metadata = (TestMetadata)leaf.Test;
            if (missingFullName) metadata.FullName = string.Empty;
            else metadata.UniqueName = string.Empty;
            var evidence = new TestRunNonPassingEvidence();

            evidence.CaptureTree(Suite("Root", new object[] { leaf }));

            var document = Read(evidence);
            AssertEvidence(document, 1, "complete", false);
            Assert.That(document.non_passing_tests[0].full_name, Is.EqualTo(missingFullName ? string.Empty : identity));
            Assert.That(document.non_passing_tests[0].unique_name, Is.EqualTo(missingFullName ? identity : string.Empty));
        }

        [Test]
        public void NameAndToStringCannotInventAnExecutableIdentity()
        {
            var leaf = Leaf(string.Empty, "Ignored");
            var metadata = (TestMetadata)leaf.Test;
            metadata.Name = "Looks.Like.A.Test";
            leaf.FullName = "Forbidden.Flat.Fallback";
            leaf.UniqueName = "Forbidden.Flat.Fallback";
            var evidence = new TestRunNonPassingEvidence();

            evidence.CaptureTree(Suite("Root", new object[] { leaf }));

            AssertEvidence(Read(evidence), 0, "partial", false);
            Assert.That(Serialize(evidence), Does.Not.Contain("Invented.From.ToString"));
            Assert.That(Serialize(evidence), Does.Not.Contain("Forbidden.Flat.Fallback"));
        }

        [Test]
        public void ExactIdentityBoundsAndClippedDiagnosticBoundsAreIndependent()
        {
            var boundary = Leaf(new string('f', 2048), "Skipped:" + new string('s', 112), new string('a', 256));
            var boundaryMetadata = (TestMetadata)boundary.Test;
            boundaryMetadata.UniqueName = new string('u', 2048);
            boundaryMetadata.Name = new string('n', 200);
            boundary.Message = new string('m', 1000);
            boundary.StackTrace = new string('t', 2000);
            var evidence = new TestRunNonPassingEvidence();
            evidence.CaptureTree(Suite("Root", new object[] { boundary }));
            var valid = Read(evidence);
            AssertEvidence(valid, 1, "complete", false);
            Assert.That(valid.non_passing_tests[0].full_name, Is.EqualTo(boundaryMetadata.FullName));
            Assert.That(valid.non_passing_tests[0].unique_name, Is.EqualTo(boundaryMetadata.UniqueName));
            Assert.That(valid.non_passing_tests[0].assembly.Length, Is.EqualTo(256));
            Assert.That(valid.non_passing_tests[0].raw_status.Length, Is.EqualTo(120));

            foreach (var field in new[] { "full", "unique", "assembly", "status" })
            {
                var invalid = Leaf("Exact.Identity", "Ignored");
                var metadata = (TestMetadata)invalid.Test;
                if (field == "full") metadata.FullName = new string('f', 2049);
                if (field == "unique") metadata.UniqueName = new string('u', 2049);
                if (field == "assembly") metadata.AssemblyName = new string('a', 257);
                if (field == "status")
                {
                    invalid.TestStatus = "Skipped:" + new string('s', 113);
                    invalid.ResultState = new AdapterResultState(invalid.TestStatus);
                }
                evidence.CaptureTree(Suite("Root", new object[] { invalid, Leaf("Valid.Sibling", "Error") }));
                var partial = Read(evidence);
                Assert.That(partial.non_passing_evidence_status, Is.EqualTo("partial"), field);
                Assert.That(partial.non_passing_tests.Length, Is.EqualTo(1), field);
                Assert.That(partial.non_passing_tests[0].full_name, Is.EqualTo("Valid.Sibling"), field);
            }

            var clipped = Leaf("Exact.Unclipped.Identity", "Inconclusive");
            ((TestMetadata)clipped.Test).Name = new string('n', 201);
            clipped.Message = new string('m', 1001);
            clipped.StackTrace = new string('t', 2001);
            evidence.CaptureTree(Suite("Root", new object[] { clipped }));
            var projected = Read(evidence);
            AssertEvidence(projected, 1, "truncated", true);
            Assert.That(projected.non_passing_tests[0].name, Is.EqualTo(new string('n', 200)));
            Assert.That(projected.non_passing_tests[0].message, Is.EqualTo(new string('m', 1000)));
            Assert.That(projected.non_passing_tests[0].stack_trace, Is.EqualTo(new string('t', 2000)));
            Assert.That(projected.non_passing_tests[0].full_name, Is.EqualTo("Exact.Unclipped.Identity"));

            // The pair crosses the clip boundary; invalid text beyond the clip
            // must not discard an otherwise valid retained diagnostic prefix.
            var invalidTail = new string('\ud800', 10000);
            ((TestMetadata)clipped.Test).Name = new string('n', 199) + "\ud83d\ude80" + invalidTail;
            clipped.Message = new string('m', 999) + "\ud83d\ude80" + invalidTail;
            clipped.StackTrace = new string('t', 1999) + "\ud83d\ude80" + invalidTail;
            evidence.CaptureTree(Suite("Root", new object[] { clipped }));
            var safe = Read(evidence);
            AssertEvidence(safe, 1, "truncated", true);
            Assert.That(safe.non_passing_tests[0].name, Is.EqualTo(new string('n', 199)));
            Assert.That(safe.non_passing_tests[0].message, Is.EqualTo(new string('m', 999)));
            Assert.That(safe.non_passing_tests[0].stack_trace, Is.EqualTo(new string('t', 1999)));
        }

        [Test]
        public void CallbackAndTerminalDedupDoesNotCertifyObsoleteCallbackRecords()
        {
            var evidence = new TestRunNonPassingEvidence();
            evidence.Capture(Leaf("Same.Case", "Failed", "Callback.Assembly"));
            evidence.Capture(Leaf("Same.Case", "Failed", "Callback.Assembly"));
            AssertEvidence(Read(evidence), 1, "partial", false);

            evidence.CaptureTree(Suite("Terminal.Root", new object[]
            {
                Leaf("Same.Case", "Ignored", "Terminal.Assembly"),
                Leaf("Same.Case", "Ignored", "Terminal.Assembly"),
            }));

            var document = Read(evidence);
            AssertEvidence(document, 1, "complete", false);
            Assert.That(document.non_passing_tests[0].assembly, Is.EqualTo("Terminal.Assembly"));
            Assert.That(document.non_passing_tests[0].raw_status, Is.EqualTo("Ignored"));
            evidence.CaptureTree(Suite("Terminal.Empty", new object[0]));
            AssertEvidence(Read(evidence), 0, "complete", false);
        }

        [Test]
        public void DedupUsesTheExactAssemblyFullUniqueAndWholeRawStatusTuple()
        {
            var leaves = new object[]
            {
                Leaf("Same.Full", "Skipped:Ignored", "Assembly.A", "Same.Unique"),
                Leaf("Same.Full", "Skipped:Ignored", "Assembly.B", "Same.Unique"),
                Leaf("Other.Full", "Skipped:Ignored", "Assembly.A", "Same.Unique"),
                Leaf("Same.Full", "Skipped:Ignored", "Assembly.A", "Other.Unique"),
                Leaf("Same.Full", "Skipped:Inconclusive", "Assembly.A", "Same.Unique"),
                Leaf("Same.Full", "Ignored", "Assembly.A", "Same.Unique"),
                Leaf("Same.Full", "Skipped:Ignored", "Assembly.A", "Same.Unique"),
            };
            var evidence = new TestRunNonPassingEvidence();

            evidence.CaptureTree(Suite("Root", leaves));

            AssertEvidence(Read(evidence), 6, "complete", false);
        }

        [TestCase(63)]
        [TestCase(64)]
        [TestCase(65)]
        public void CapacityDeduplicatesBeforeAdmissionAndRetainsOnlyBoundedKeys(int uniqueCount)
        {
            var children = new List<object>();
            for (var index = 0; index < uniqueCount; index++) children.Add(Leaf("Capacity.Case" + index, "Ignored"));
            // A duplicate encountered after a full store must not falsely truncate.
            children.Add(Leaf("Capacity.Case0", "Ignored"));
            var evidence = new TestRunNonPassingEvidence();

            evidence.CaptureTree(Suite("Root", children));

            AssertEvidence(Read(evidence), Math.Min(uniqueCount, EntryLimit), uniqueCount > EntryLimit ? "truncated" : "complete", uniqueCount > EntryLimit);
            if (uniqueCount == 65)
            {
                var callbackOnly = new TestRunNonPassingEvidence();
                for (var index = 0; index < 1024; index++) callbackOnly.Capture(Leaf("Omitted.Case" + index, "Ignored"));
                AssertEvidence(Read(callbackOnly), EntryLimit, "partial", true);
                AssertRetainedCollectionsBounded(callbackOnly);
            }
        }

        [Test]
        public void TreeAttemptBudgetIncludesNullAndRepeatedObjectsWithoutAnUnboundedEnumerator()
        {
            var stream = new BudgetEnumerable(Leaf("Repeated.Case", "Ignored"));
            var evidence = new TestRunNonPassingEvidence();

            Assert.DoesNotThrow(() => evidence.CaptureTree(Suite("Root", stream)));

            var document = Read(evidence);
            AssertEvidence(document, 1, "partial", true);
            Assert.That(stream.MoveNextCalls, Is.LessThanOrEqualTo(NodeLimit + 1));
        }

        [Test]
        public void TreeDepthUsesRootZeroAndOmitsOnlyBeyondDepth64()
        {
            var evidence = new TestRunNonPassingEvidence();
            evidence.CaptureTree(DepthChain(DepthLimit));
            AssertEvidence(Read(evidence), 1, "complete", false);

            evidence.CaptureTree(DepthChain(DepthLimit + 1));

            AssertEvidence(Read(evidence), 0, "truncated", true);
        }

        [Test]
        public void ReferenceCycleIsPartialWithoutBudgetTruncationAndRetainsValidSiblings()
        {
            var root = Suite("Root", new object[0]);
            root.Children = new object[] { root, Leaf("Valid.Sibling", "Ignored") };
            var evidence = new TestRunNonPassingEvidence();

            Assert.DoesNotThrow(() => evidence.CaptureTree(root));

            AssertEvidence(Read(evidence), 1, "partial", false);
        }

        [Test]
        public void SharedReferencesArePartialButValueEqualDistinctNodesAreNotShared()
        {
            var evidence = new TestRunNonPassingEvidence();
            var shared = Leaf("Shared.Case", "Inconclusive");
            evidence.CaptureTree(Suite("Root", new object[] { shared, shared }));
            AssertEvidence(Read(evidence), 1, "partial", false);

            evidence.CaptureTree(Suite("Root", new object[] { ValueEqualLeaf("Distinct.One"), ValueEqualLeaf("Distinct.Two") }));

            AssertEvidence(Read(evidence), 2, "complete", false);
        }

        [Test]
        public void NullTerminalTreeResetsEvenPreviouslyCompleteEvidenceToUnavailable()
        {
            var evidence = new TestRunNonPassingEvidence();
            evidence.CaptureTree(Suite("Root", new object[] { Leaf("Old.Terminal", "Ignored") }));
            AssertEvidence(Read(evidence), 1, "complete", false);

            evidence.CaptureTree(null);

            AssertEvidence(Read(evidence), 0, "unavailable", false);
        }

        [Test]
        public void StructuralMetadataFaultsRemainPartialAndMalformedLeavesTraverseOnlyTerminalChildren()
        {
            foreach (var bad in new object[]
            {
                new ResultNode { Test = new MetadataWithoutSuiteFlag(), TestStatus = "Ignored", Children = new object[0] },
                new ResultNode { Test = new MetadataWithWrongSuiteFlag(), TestStatus = "Ignored", Children = new object[0] },
                new ResultNode { Test = new MetadataWithThrowingSuiteFlag(), TestStatus = "Ignored", Children = new object[0] },
                new ResultWithMissingChildren(), new ResultWithThrowingChildren(),
                new ResultNode { Test = Metadata("Null.Children", true), TestStatus = "Failed", Children = null },
                new ResultWithThrowingStatus(),
            })
            {
                var evidence = new TestRunNonPassingEvidence();
                Assert.DoesNotThrow(() => evidence.CaptureTree(Suite("Root", new object[] { bad, Leaf("Valid.Sibling", "Ignored") })));
                var document = Read(evidence);
                AssertEvidence(document, 1, "partial", false);
                Assert.That(document.non_passing_tests[0].full_name, Is.EqualTo("Valid.Sibling"));
            }

            var malformed = Leaf("Malformed.Parent", "Failed");
            malformed.Children = new object[] { Leaf("Valid.Child", "Ignored") };
            var terminal = new TestRunNonPassingEvidence();
            terminal.CaptureTree(Suite("Root", new object[] { malformed }));
            var traversed = Read(terminal);
            AssertEvidence(traversed, 1, "partial", false);
            Assert.That(traversed.non_passing_tests[0].full_name, Is.EqualTo("Valid.Child"));

            // Missing/null children are not proof of an observed empty suite.
            terminal.CaptureTree(new ResultWithMissingChildren());
            AssertEvidence(Read(terminal), 0, "partial", false);
            terminal.CaptureTree(new ResultNode { Test = Metadata("Null.Root", true), TestStatus = "Passed", Children = null });
            AssertEvidence(Read(terminal), 0, "partial", false);
        }

        [Test]
        public void EnumerationAndDisposalFaultsAreContainedAndKeepOuterValidSiblings()
        {
            foreach (var stage in new[] { "get", "move", "current", "dispose" })
            {
                var evidence = new TestRunNonPassingEvidence();
                var brokenSuite = Suite("Broken.Suite", new FaultEnumerable(stage));

                Assert.DoesNotThrow(() => evidence.CaptureTree(Suite("Root", new object[] { brokenSuite, Leaf("Valid.Sibling", "Ignored") })), stage);

                var document = Read(evidence);
                AssertEvidence(document, 1, "partial", false);
                Assert.That(document.non_passing_tests[0].full_name, Is.EqualTo("Valid.Sibling"), stage);
            }
        }

        [Test]
        public void UntouchedLegacyMissingOrMisTypedEvidenceHeadersAreUnavailable()
        {
            var evidence = new TestRunNonPassingEvidence();
            AssertEvidence(Read(evidence), 0, "unavailable", false);
            foreach (var receipt in new[]
            {
                "{\"command\":\"test.run\",\"total\":4,\"success\":true}",
                "{\"nested\":{\"non_passing_tests\":[],\"non_passing_tests_truncated\":false,\"non_passing_evidence_status\":\"complete\"}}",
                "{\"non_passing_tests\":[],\"non_passing_evidence_status\":\"complete\"}",
                "{\"non_passing_tests_truncated\":false,\"non_passing_evidence_status\":\"complete\"}",
                "{\"non_passing_tests\":[],\"non_passing_tests_truncated\":false}",
                "{\"non_passing_tests\":null,\"non_passing_tests_truncated\":false,\"non_passing_evidence_status\":\"complete\"}",
                "{\"non_passing_tests\":[],\"non_passing_tests_truncated\":\"false\",\"non_passing_evidence_status\":\"complete\"}",
                "{\"non_passing_tests\":[],\"non_passing_tests_truncated\":false,\"non_passing_evidence_status\":true}",
                "{\"non_passing_tests\":[],\"non_passing_tests_truncated\":false,\"non_passing_evidence_status\":\"unknown\"}",
            })
            {
                evidence.Capture(Leaf("Discarded.Old", "Ignored"));
                evidence.Restore(receipt);
                AssertEvidence(Read(evidence), 0, "unavailable", false);
            }
        }

        [Test]
        public void PersistedRoundTripPreservesEscapedUnicodeExactIdentityAndEvidenceProvenance()
        {
            const string fullName = "Fixture.Case(\"雪\\line\n\ud83d\ude80\")";
            const string rawStatus = "Inconclusive:reason \"quoted\"\\path\n雪";
            var leaf = Leaf(fullName, rawStatus, "Actual.雪.Assembly", fullName + "#unique");
            ((TestMetadata)leaf.Test).Name = "Case \"雪\"\\\n\ud83d\ude80";
            leaf.Message = "message \"quoted\"\\path\n雪\t\ud83d\ude80";
            leaf.StackTrace = "stack\r\nC:\\源\\file.cs\t\ud83d\ude80";
            var evidence = new TestRunNonPassingEvidence();
            evidence.CaptureTree(Suite("Root", new object[] { leaf, Leaf("Fixture.Other", "Ignored") }));
            var original = Read(evidence);
            var restored = new TestRunNonPassingEvidence();

            restored.Restore(Serialize(evidence));

            AssertEvidence(Read(restored), 2, "complete", false);
            AssertEntriesEqual(Read(restored).non_passing_tests, original.non_passing_tests);
            restored.Capture(leaf);
            AssertEvidence(Read(restored), 2, "partial", false);
            AssertEntriesEqual(Read(restored).non_passing_tests, original.non_passing_tests);

            var entry = EntryJson("Restored.One", "Ignored");
            restored.Restore(Receipt(entry, "complete", true));
            AssertEvidence(Read(restored), 1, "truncated", true);
            restored.Restore(Receipt(entry, "partial", true));
            AssertEvidence(Read(restored), 1, "partial", true);
        }

        [Test]
        public void StrictRestoreRejectsMalformedGrammarDuplicateDecodedKeysAndParserBudgets()
        {
            var valid = Receipt(EntryJson("Valid.One", "Ignored"));
            var tooManyNodes = valid.Substring(0, valid.Length - 1) + ",\"other\":[" + string.Join(",", new string[NodeLimit + 1]).Replace(",", "null,") + "null]}";
            var tooDeep = valid.Substring(0, valid.Length - 1) + ",\"other\":" + NestedJson(40) + "}";
            var tooLargeUtf8 = valid.Substring(0, valid.Length - 1) + ",\"other\":\"" + new string('雪', 70000) + "\"}";
            var tooLongCharacters = valid.Substring(0, valid.Length - 1) + ",\"other\":\"" + new string('a', ArtifactByteLimit + 1) + "\"}";
            var invalidReceipts = new[]
            {
                string.Empty, "[]", "null", "true", "17", "\"string root\"", "{", valid + " trailing", valid + "{}",
                valid.Substring(0, valid.Length - 1), valid.Substring(0, valid.Length - 1) + ",}",
                valid.Replace("\"non_passing_tests\":", "\"non_passing_tests\":[],\"non_passing_tests\":"),
                valid.Replace("\"non_passing_tests\":", "\"non_passing_tests\":[],\"non_passing_\\u0074ests\":"),
                valid.Substring(0, valid.Length - 1) + ",\"other\":{\"a\":1,\"\\u0061\":2}}",
                valid.Replace("\"message\":\"ok\"", "\"message\":\"\\q\""),
                valid.Replace("\"message\":\"ok\"", "\"message\":\"\\u12xz\""),
                valid.Replace("\"message\":\"ok\"", "\"message\":\"raw\ncontrol\""),
                valid.Replace("\"raw_status\":\"Ignored\"", "\"raw_status\":\"Ignored\",\"raw_status\":\"Ignored\""),
                valid.Substring(0, valid.Length - 1) + ",\"other\":01}",
                valid.Substring(0, valid.Length - 1) + ",\"other\":1.}",
                valid.Substring(0, valid.Length - 1) + ",\"other\":1e+}",
                valid.Substring(0, valid.Length - 1) + ",\"other\":[0,]}",
                tooManyNodes, tooDeep, tooLargeUtf8, tooLongCharacters,
            };
            foreach (var receipt in invalidReceipts)
            {
                var evidence = new TestRunNonPassingEvidence();
                evidence.Capture(Leaf("Discarded.Old", "Ignored"));
                Assert.DoesNotThrow(() => evidence.Restore(receipt));
                AssertEvidence(Read(evidence), 0, "unavailable", false);
            }

            var permittedDepth = valid.Substring(0, valid.Length - 1) + ",\"other\":" + NestedJson(20) + "}";
            var permitted = new TestRunNonPassingEvidence();
            permitted.Restore(permittedDepth);
            AssertEvidence(Read(permitted), 1, "complete", false);
        }

        [Test]
        public void TypedInvalidEntriesKeepValidSiblingsAndRestoredDedupPrecedesCapacity()
        {
            var first = EntryJson("Valid.One", "Ignored");
            var second = EntryJson("Valid.Two", "Inconclusive");
            var evidence = new TestRunNonPassingEvidence();

            foreach (var bad in new[]
            {
                first.Replace("\"message\":\"ok\"", "\"message\":17"),
                first.Replace("\"full_name\":\"Valid.One\"", "\"full_name\":null"),
                first.Replace("\"full_name\":\"Valid.One\"", "\"full_name\":\"" + new string('f', 2049) + "\""),
                first.Replace("\"unique_name\":\"Valid.One\"", "\"unique_name\":\"" + new string('u', 2049) + "\""),
                first.Replace("\"assembly\":\"Fixture.Editor\"", "\"assembly\":\"" + new string('a', 257) + "\""),
                first.Replace("\"raw_status\":\"Ignored\"", "\"raw_status\":\"Skipped:" + new string('s', 113) + "\""),
                first.Replace("\"raw_status\":\"Ignored\"", "\"raw_status\":\"NotIgnored\""),
                first.Replace("\"full_name\":\"Valid.One\"", "\"full_name\":\"\"").Replace("\"unique_name\":\"Valid.One\"", "\"unique_name\":\"\""),
                "null", "[]", "17",
            })
            {
                evidence.Restore(Receipt(first + "," + bad + "," + second + "," + first));
                var partial = Read(evidence);
                AssertEvidence(partial, 2, "partial", false);
                Assert.That(partial.non_passing_tests[0].full_name, Is.EqualTo("Valid.One"));
                Assert.That(partial.non_passing_tests[1].full_name, Is.EqualTo("Valid.Two"));
            }
            evidence.Capture(Leaf("Valid.One", "Ignored"));
            AssertEvidence(Read(evidence), 2, "partial", false);
            var entries = new List<string>();
            for (var index = 0; index < EntryLimit; index++) entries.Add(EntryJson("Capacity.Case" + index, "Ignored"));
            entries.Add(entries[0]);
            evidence.Restore(Receipt(string.Join(",", entries.ToArray())));
            AssertEvidence(Read(evidence), EntryLimit, "complete", false);
            entries.Add(EntryJson("Capacity.Overflow", "Ignored"));
            evidence.Restore(Receipt(string.Join(",", entries.ToArray())));
            AssertEvidence(Read(evidence), EntryLimit, "truncated", true);
            AssertRetainedCollectionsBounded(evidence);
        }

        [Test]
        public void SerializedUtf8BudgetIncludesEscapesAndProjectsWithoutDestructiveTrimming()
        {
            var children = new List<object>();
            for (var index = 0; index < EntryLimit; index++)
            {
                var identity = "Fixture." + new string('雪', 2036) + index.ToString("D4", CultureInfo.InvariantCulture);
                var leaf = Leaf(identity, "Skipped:reason \"quoted\"");
                ((TestMetadata)leaf.Test).Name = new string('\\', 200);
                leaf.Message = new string('"', 1000);
                leaf.StackTrace = new string('雪', 2000);
                children.Add(leaf);
            }
            var evidence = new TestRunNonPassingEvidence();
            evidence.CaptureTree(Suite("Root", children));
            var smallPrefixProjection = Serialize(evidence);
            var first = Decode(smallPrefixProjection);
            Assert.That(first.non_passing_tests.Length, Is.GreaterThan(0).And.LessThan(EntryLimit));
            Assert.That(first.non_passing_tests_truncated, Is.True);
            Assert.That(first.non_passing_evidence_status, Is.EqualTo("truncated"));
            AssertSerializedBudget(smallPrefixProjection, DefaultPrefix);
            var largePrefix = "{\"base\":true,\"pad\":\"" + new string('雪', 63000) + "\"";

            var nearFullArtifact = Serialize(evidence, largePrefix);

            AssertSerializedBudget(nearFullArtifact, largePrefix);
            Assert.That(nearFullArtifact.StartsWith(largePrefix, StringComparison.Ordinal), Is.True);
            var limited = Decode(nearFullArtifact);
            Assert.That(limited.non_passing_tests.Length, Is.LessThan(first.non_passing_tests.Length));
            Assert.That(limited.non_passing_tests_truncated, Is.True);
            Assert.That(limited.non_passing_evidence_status, Is.EqualTo("truncated"));
            AssertEntriesEqual(Read(evidence).non_passing_tests, first.non_passing_tests);

            // Byte projection may add a truncation flag, but cannot promote a
            // callback-only partial witness into certified terminal coverage.
            evidence.Capture(children[0]);
            var callbackProjection = Read(evidence);
            AssertEvidence(callbackProjection, first.non_passing_tests.Length, "partial", true);
            AssertEntriesEqual(callbackProjection.non_passing_tests, first.non_passing_tests);
        }

        [Test]
        public void InsufficientHeaderRoomOmitsOnlyOwnFieldsAndRestoresAsUnavailable()
        {
            const string prefix = "{\"operation_id\":\"unchanged-run\",\"run_id\":\"unchanged-run\",\"total\":9,\"passed\":2,\"failed\":0,\"skipped\":7,\"success\":true";
            var evidence = new TestRunNonPassingEvidence();
            evidence.Capture(Leaf("Retained.Case", "Ignored"));
            var bytesForBaseAndClosingOnly = Encoding.UTF8.GetByteCount(prefix) + 1;

            var projected = Serialize(evidence, prefix, bytesForBaseAndClosingOnly);

            Assert.That(projected, Is.EqualTo(prefix + "}"));
            Assert.That(Encoding.UTF8.GetByteCount(projected), Is.EqualTo(bytesForBaseAndClosingOnly));
            Assert.That(projected, Does.Not.Contain("non_passing_"));
            var legacy = new TestRunNonPassingEvidence();
            legacy.Restore(projected);
            AssertEvidence(Read(legacy), 0, "unavailable", false);

            const string invalidEncodingBase = "{\"base\":\"\ud800\"";
            string unchangedBase = null;
            Assert.DoesNotThrow(() => unchangedBase = Serialize(evidence, invalidEncodingBase));
            Assert.That(unchangedBase, Is.EqualTo(invalidEncodingBase + "}"));
            AssertEvidence(Read(evidence), 1, "partial", false);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TerminalCoverageMismatchDowngradesOnlyEvidenceAndFreshMatchingTraversalRecovers(bool nonPassingMismatch)
        {
            var state = RunState();
            state.Success = true;
            state.Status = "completed";
            state.ResultState = "Passed";
            state.ExecuteDispatchCount = 1;
            state.ExecuteCallReturned = true;
            Assert.That(BuildTestAutomationCommands.TryApplyTerminalTestCounts(state, 2, 0, 1, 1, 99, 4), Is.True);
            state.NonPassingEvidence.CaptureTree(CoverageTree());
            state.NonPassingEvidence.CheckTerminalCoverage(state.Total, state.Failed + state.Skipped);
            var matching = Read(state.NonPassingEvidence);
            AssertEvidence(matching, 2, "complete", false);

            // Each case disagrees on exactly one dimension: total leaves or
            // non-passing leaves. Inconclusive is already included in Skipped.
            Assert.That(BuildTestAutomationCommands.TryApplyTerminalTestCounts(state,
                nonPassingMismatch ? 1 : 3, nonPassingMismatch ? 1 : 0, 1, 1, 99, nonPassingMismatch ? 4 : 5), Is.True);
            var before = StateAuthority(state);
            state.NonPassingEvidence.CheckTerminalCoverage(state.Total, state.Failed + state.Skipped);

            var partial = Read(state.NonPassingEvidence);
            AssertEvidence(partial, 2, "partial", false);
            AssertEntriesEqual(partial.non_passing_tests, matching.non_passing_tests);
            Assert.That(StateAuthority(state), Is.EqualTo(before));
            state.NonPassingEvidence.CheckTerminalCoverage(4, 2);
            AssertEvidence(Read(state.NonPassingEvidence), 2, "partial", false);

            Assert.That(BuildTestAutomationCommands.TryApplyTerminalTestCounts(state, 2, 0, 1, 1, 99, 4), Is.True);
            state.NonPassingEvidence.CaptureTree(CoverageTree());
            state.NonPassingEvidence.CheckTerminalCoverage(state.Total, state.Failed + state.Skipped);
            AssertEvidence(Read(state.NonPassingEvidence), 2, "complete", false);

            // The naked helper can certify an observed empty suite. Positive
            // authoritative BTA counts then downgrade only its coverage witness.
            state.NonPassingEvidence.CaptureTree(Suite("Empty.Terminal", new object[0]));
            state.NonPassingEvidence.CheckTerminalCoverage(0, 0);
            AssertEvidence(Read(state.NonPassingEvidence), 0, "complete", false);
            before = StateAuthority(state);
            state.NonPassingEvidence.CheckTerminalCoverage(state.Total, state.Failed + state.Skipped);
            AssertEvidence(Read(state.NonPassingEvidence), 0, "partial", false);
            Assert.That(StateAuthority(state), Is.EqualTo(before));

            state.NonPassingEvidence.CaptureTree(Suite("Unknown.Terminal", new object[] { Leaf("Unknown.Leaf", "NotFailed") }));
            state.NonPassingEvidence.CheckTerminalCoverage(1, 0);
            AssertEvidence(Read(state.NonPassingEvidence), 0, "partial", false);
        }

        [Test]
        public void NewCollectorMetadataFaultsDoNotChangeLegacyCallbackCountsOrRunOutcome()
        {
            var state = RunState();
            state.Passed = state.Total = 2;
            state.Success = true;
            state.ExecuteDispatchCount = 1;
            state.ExecuteCallReturned = true;
            var callback = new TestRunCallbacksProxy();
            var field = typeof(TestRunCallbacksProxy).GetField("m_state", BindingFlags.Instance | BindingFlags.NonPublic);
            var method = typeof(TestRunCallbacksProxy).GetMethod("CaptureTestResult", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            Assert.That(method, Is.Not.Null);
            field.SetValue(callback, state);
            var result = new ThrowingTestResult();

            // Only Test metadata throws. The legacy status getter is healthy;
            // this does not assert that old callback getter faults are repaired.
            Assert.DoesNotThrow(() => method.Invoke(callback, new object[] { result }));

            Assert.That(state.Passed, Is.EqualTo(2));
            Assert.That(state.Failed, Is.Zero);
            Assert.That(state.Skipped, Is.EqualTo(1));
            Assert.That(state.Total, Is.EqualTo(3));
            Assert.That(state.Success, Is.True);
            Assert.That(state.Status, Is.EqualTo("running"));
            Assert.That(state.ExecuteDispatchCount, Is.EqualTo(1));
            Assert.That(state.ExecuteCallReturned, Is.True);
            Assert.That(result.TestGetterCalls, Is.GreaterThan(0));
            AssertEvidence(Read(state.NonPassingEvidence), 0, "partial", false);

            var before = StateAuthority(state);
            Assert.DoesNotThrow(() => state.NonPassingEvidence.CaptureTree(Suite("Broken.Terminal", new FaultEnumerable("move"))));
            Assert.That(StateAuthority(state), Is.EqualTo(before));
            AssertEvidence(Read(state.NonPassingEvidence), 0, "partial", false);
        }

        [Test]
        public void BtaSerializeAndRestoreAddEvidenceWithoutChangingOwnerCountsSuccessOrExecute()
        {
            var state = RunState();
            state.TestAssembly = "Requested.Filter.Assembly";
            state.Success = true;
            state.ArtifactPublicationState = "committed";
            state.ExecuteDispatchCount = 1;
            state.ExecuteCallReturned = true;
            Assert.That(BuildTestAutomationCommands.TryApplyTerminalTestCounts(state, 2, 0, 1, 1, 99, 4), Is.True);
            var baselineJson = BuildTestAutomationCommands.TestRunStateJson(state);
            var baselineRestore = BuildTestAutomationCommands.TestRunStateFromPersistedArtifact(state.RunId, state.ArtifactPath, baselineJson);
            var before = StateAuthority(state);

            state.NonPassingEvidence.CaptureTree(CoverageTree());
            state.NonPassingEvidence.CheckTerminalCoverage(state.Total, state.Failed + state.Skipped);
            var receipt = BuildTestAutomationCommands.TestRunStateJson(state);
            var restored = BuildTestAutomationCommands.TestRunStateFromPersistedArtifact(state.RunId, state.ArtifactPath, receipt);

            Assert.That(StateAuthority(state), Is.EqualTo(before));
            Assert.That(StateAuthority(restored), Is.EqualTo(StateAuthority(baselineRestore)));
            Assert.That(receipt, Does.Contain("\"success\":true"));
            Assert.That(receipt, Does.Contain("\"test_result_success\":true"));
            Assert.That(restored.Total, Is.EqualTo(4));
            Assert.That(restored.Passed, Is.EqualTo(2));
            Assert.That(restored.Skipped, Is.EqualTo(2));
            Assert.That(restored.InconclusiveCount, Is.EqualTo(1));
            Assert.That(restored.AssertCount, Is.EqualTo(99));
            AssertEvidence(Decode(receipt), 2, "complete", false);
            var recovered = Read(restored.NonPassingEvidence);
            AssertEvidence(recovered, 2, "complete", false);
            foreach (var entry in recovered.non_passing_tests) Assert.That(entry.assembly, Is.EqualTo("Observed.Assembly"));
        }

        static ResultNode CoverageTree()
        {
            return Suite("Observed.Assembly", new object[]
            {
                Leaf("Fixture.PassedOne", "Passed", string.Empty),
                Leaf("Fixture.PassedTwo", "Passed", string.Empty),
                Leaf("Fixture.Inconclusive", "Inconclusive", string.Empty),
                Leaf("Fixture.Ignored", "Ignored", string.Empty),
            }, "Passed", "Observed.Assembly", true);
        }

        static string Serialize(TestRunNonPassingEvidence evidence, string prefix = DefaultPrefix, int maxArtifactBytes = ArtifactByteLimit)
        {
            var builder = new StringBuilder(prefix);
            evidence.AppendJson(builder, maxArtifactBytes);
            builder.Append('}');
            return builder.ToString();
        }

        static EvidenceDocument Read(TestRunNonPassingEvidence evidence)
        {
            return Decode(Serialize(evidence));
        }

        static EvidenceDocument Decode(string json)
        {
            // Decode only generated output. Strict input acceptance is exercised
            // through Restore, not inferred from JsonUtility's parser behavior.
            var result = JsonUtility.FromJson<EvidenceDocument>(json);
            Assert.That(result, Is.Not.Null);
            return result;
        }

        static void AssertEvidence(EvidenceDocument document, int entries, string status, bool truncated)
        {
            Assert.That(document.non_passing_tests, Is.Not.Null);
            Assert.That(document.non_passing_tests.Length, Is.EqualTo(entries));
            Assert.That(document.non_passing_evidence_status, Is.EqualTo(status));
            Assert.That(document.non_passing_tests_truncated, Is.EqualTo(truncated));
        }

        static void AssertEntriesEqual(EvidenceEntry[] actual, EvidenceEntry[] expected)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length));
            for (var index = 0; index < expected.Length; index++)
            {
                Assert.That(JsonUtility.ToJson(actual[index]), Is.EqualTo(JsonUtility.ToJson(expected[index])));
            }
        }

        static void AssertSerializedBudget(string json, string prefix)
        {
            var bytes = Encoding.UTF8.GetByteCount(json);
            Assert.That(bytes, Is.LessThanOrEqualTo(ArtifactByteLimit));
            Assert.That(bytes - Encoding.UTF8.GetByteCount(prefix) - 1, Is.LessThanOrEqualTo(AdditionByteLimit));
            Assert.That(json.StartsWith(prefix, StringComparison.Ordinal), Is.True);
        }

        static void AssertRetainedCollectionsBounded(TestRunNonPassingEvidence evidence)
        {
            // Observe storage size without introducing a production inspection
            // API or assuming the private dedup collection's field name.
            var observedCollections = 0;
            foreach (var field in typeof(TestRunNonPassingEvidence).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var value = field.GetValue(evidence);
                if (!(value is IEnumerable) || value is string) continue;
                var count = value.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
                if (count == null || count.PropertyType != typeof(int)) continue;
                observedCollections++;
                Assert.That((int)count.GetValue(value, null), Is.LessThanOrEqualTo(EntryLimit), field.Name);
            }
            Assert.That(observedCollections, Is.GreaterThan(0));
        }

        static string Receipt(string entries, string status = "complete", bool truncated = false)
        {
            return "{\"non_passing_tests\":[" + entries + "],\"non_passing_tests_truncated\":" + (truncated ? "true" : "false") + ",\"non_passing_evidence_status\":\"" + status + "\"}";
        }

        static string EntryJson(string fullName, string rawStatus)
        {
            return JsonUtility.ToJson(new EvidenceEntry { name = "Case", full_name = fullName, unique_name = fullName, assembly = "Fixture.Editor", raw_status = rawStatus, message = "ok", stack_trace = "trace" });
        }

        static string NestedJson(int depth)
        {
            return new string('[', depth) + "0" + new string(']', depth);
        }

        static ResultNode Leaf(string fullName, string status, string assembly = "Fixture.Editor", string uniqueName = null)
        {
            return new ResultNode { Test = Metadata(fullName, false, assembly, uniqueName), TestStatus = status, ResultState = new AdapterResultState(status), Children = new object[0], Message = "ok", StackTrace = "trace" };
        }

        static ResultNode Suite(string fullName, IEnumerable children, string status = "Passed", string assembly = "Fixture.Editor", bool isAssembly = false)
        {
            return new ResultNode { Test = Metadata(fullName, true, assembly, null, isAssembly), TestStatus = status, ResultState = new AdapterResultState(status), Children = children };
        }

        static TestMetadata Metadata(string fullName, bool suite, string assembly = "Fixture.Editor", string uniqueName = null, bool isAssembly = false)
        {
            return new TestMetadata { Name = "Case", FullName = fullName, UniqueName = uniqueName ?? fullName, AssemblyName = assembly, IsSuite = suite, IsTestAssembly = isAssembly };
        }

        static ResultNode DepthChain(int leafDepth)
        {
            var result = Leaf("Depth.Leaf", "Ignored");
            for (var depth = leafDepth - 1; depth >= 0; depth--) result = Suite("Depth.Suite" + depth, new object[] { result });
            return result;
        }

        static ResultNode ValueEqualLeaf(string fullName)
        {
            return new ValueEqualResultNode { Test = Metadata(fullName, false), TestStatus = "Ignored", ResultState = new AdapterResultState("Ignored"), Children = new object[0] };
        }

        static BuildTestAutomationCommands.TestRunState RunState()
        {
            const string runId = "test-20261007-120000-deadbeef";
            return new BuildTestAutomationCommands.TestRunState(runId, "editmode", string.Empty, "Library/SGGUnityMcp/job-artifacts/test-runs/" + runId + ".json", 60, false)
            {
                ArgsHash = Hash, CallerArgsHash = Hash, CreatedAt = "2026-10-07T12:00:00.0000000Z",
                StartedAt = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc),
            };
        }

        static object[] StateAuthority(BuildTestAutomationCommands.TestRunState state)
        {
            return new object[]
            {
                state.RunId, state.Mode, state.Filter, state.TestAssembly, state.ArtifactPath, state.ArgsHash, state.CallerArgsHash,
                state.Status, state.ResultState, state.Success, state.Total, state.Passed, state.Failed, state.Skipped,
                state.TerminalCountsAuthoritative, state.PassCount, state.FailCount, state.SkipCount, state.InconclusiveCount,
                state.AssertCountAvailable, state.AssertCount, state.TestCaseCountAvailable, state.TestCaseCount,
                state.ExecuteDispatchCount, state.ExecuteCallReturned, state.CancelRequested,
            };
        }

        [Serializable]
        public sealed class EvidenceDocument
        {
            public EvidenceEntry[] non_passing_tests;
            public bool non_passing_tests_truncated;
            public string non_passing_evidence_status;
        }

        [Serializable]
        public sealed class EvidenceEntry
        {
            public string name;
            public string full_name;
            public string unique_name;
            public string assembly;
            public string raw_status;
            public string message;
            public string stack_trace;
        }

        class ResultNode
        {
            public object Test { get; set; }
            public string Name { get; set; }
            public string FullName { get; set; }
            public string UniqueName { get; set; }
            public string AssemblyName { get; set; }
            public bool IsSuite { get; set; }
            public bool IsTestAssembly { get; set; }
            public string TestStatus { get; set; }
            public object ResultState { get; set; }
            public string Outcome { get; set; }
            public string Message { get; set; }
            public string StackTrace { get; set; }
            public IEnumerable Children { get; set; }
            public override string ToString() { return "Invented.From.ToString"; }
        }

        sealed class ValueEqualResultNode : ResultNode
        {
            public override bool Equals(object other) { return other is ValueEqualResultNode; }
            public override int GetHashCode() { return 17; }
        }

        sealed class AdapterResultState
        {
            readonly string m_state;
            public AdapterResultState(string state) { m_state = state; }
            public override string ToString() { return m_state; }
        }

        class TestMetadata
        {
            public string Name { get; set; }
            public string FullName { get; set; }
            public string UniqueName { get; set; }
            public string AssemblyName { get; set; }
            public bool IsSuite { get; set; }
            public bool IsTestAssembly { get; set; }
            public override string ToString() { return "Invented.From.ToString"; }
        }

        sealed class FlatResultWithoutTest
        {
            public string Name { get { return "Flat"; } }
            public string FullName { get { return "Flat.Exact"; } }
            public string UniqueName { get { return "Flat.Unique"; } }
            public string AssemblyName { get { return "Flat.Assembly"; } }
            public bool IsSuite { get { return false; } }
            public string TestStatus { get { return "Skipped"; } }
            public IEnumerable Children { get { return new object[0]; } }
        }

        sealed class ThrowingTestResult
        {
            public int TestGetterCalls;
            public object Test { get { TestGetterCalls++; throw new InvalidOperationException("synthetic Test getter fault"); } }
            public string FullName { get { return "Forbidden.Flat.Fallback"; } }
            public string UniqueName { get { return "Forbidden.Flat.Fallback"; } }
            public string AssemblyName { get { return "Flat.Assembly"; } }
            public bool IsSuite { get { return false; } }
            public string TestStatus { get { return "Inconclusive"; } }
            public IEnumerable Children { get { return new object[0]; } }
        }

        class MetadataWithoutSuiteFlag
        {
            public string Name { get { return "Missing.Suite.Flag"; } }
            public string FullName { get { return "Bad.Metadata"; } }
            public string UniqueName { get { return "Bad.Metadata"; } }
            public string AssemblyName { get { return "Fixture.Editor"; } }
        }

        sealed class MetadataWithWrongSuiteFlag : MetadataWithoutSuiteFlag
        {
            public string IsSuite { get { return "false"; } }
        }

        sealed class MetadataWithThrowingSuiteFlag : MetadataWithoutSuiteFlag
        {
            public bool IsSuite { get { throw new InvalidOperationException("synthetic IsSuite getter fault"); } }
        }

        class ResultWithMissingChildren
        {
            public object Test { get { return Metadata("Bad.Children", true); } }
            public string TestStatus { get { return "Passed"; } }
        }

        sealed class ResultWithThrowingChildren : ResultWithMissingChildren
        {
            public IEnumerable Children { get { throw new InvalidOperationException("synthetic Children getter fault"); } }
        }

        sealed class ResultWithThrowingStatus
        {
            public object Test { get { return Metadata("Bad.Status", false); } }
            public string TestStatus { get { throw new InvalidOperationException("synthetic status getter fault"); } }
            public IEnumerable Children { get { return new object[0]; } }
        }

        sealed class BudgetEnumerable : IEnumerable
        {
            readonly object m_repeated;
            public int MoveNextCalls;
            public BudgetEnumerable(object repeated) { m_repeated = repeated; }
            public IEnumerator GetEnumerator() { return new Enumerator(this); }
            sealed class Enumerator : IEnumerator, IDisposable
            {
                readonly BudgetEnumerable m_owner;
                public Enumerator(BudgetEnumerable owner) { m_owner = owner; }
                public object Current { get { return m_owner.MoveNextCalls % 2 == 0 ? null : m_owner.m_repeated; } }
                public bool MoveNext()
                {
                    m_owner.MoveNextCalls++;
                    // A deterministic fuse prevents a broken traversal from
                    // hanging NUnit; this makes no wall-clock bound claim.
                    if (m_owner.MoveNextCalls > NodeLimit + 1) throw new InvalidOperationException("synthetic traversal fuse exceeded");
                    return true;
                }
                public void Reset() { throw new NotSupportedException(); }
                public void Dispose() { }
            }
        }

        sealed class FaultEnumerable : IEnumerable
        {
            readonly string m_stage;
            public FaultEnumerable(string stage) { m_stage = stage; }
            public IEnumerator GetEnumerator()
            {
                if (m_stage == "get") throw new InvalidOperationException("synthetic GetEnumerator fault");
                return new Enumerator(m_stage);
            }
            sealed class Enumerator : IEnumerator, IDisposable
            {
                readonly string m_stage;
                bool m_moved;
                public Enumerator(string stage) { m_stage = stage; }
                public object Current { get { if (m_stage == "current") throw new InvalidOperationException("synthetic Current fault"); return null; } }
                public bool MoveNext()
                {
                    if (m_stage == "move") throw new InvalidOperationException("synthetic MoveNext fault");
                    if (m_stage == "dispose" || m_moved) return false;
                    m_moved = true;
                    return true;
                }
                public void Reset() { throw new NotSupportedException(); }
                public void Dispose() { if (m_stage == "dispose") throw new InvalidOperationException("synthetic Dispose fault"); }
            }
        }
    }
}
