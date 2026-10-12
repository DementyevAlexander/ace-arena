using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Commands;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class McpPr211DynamicCSharpResultSerializationTests
    {
        static int s_executeCount;
        static object s_parameter;
        static string s_consoleText;
        const int ConsoleBudget = 6500;
        const string StringLimitReason = "max_string_json_content_bytes";
        const string StringAnnotation = "\"reason\":\"max_string_json_content_bytes\"";
        static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        [TestCase(99, 99, false)]
        [TestCase(100, 100, false)]
        [TestCase(101, 100, true)]
        [TestCase(1545, 100, true)]
        public void ArraysExposeExactCountsAndTruncation(int totalCount, int returnedCount, bool truncated)
        {
            var result = DynamicCSharpResultSerializer.Serialize(Enumerable.Range(0, totalCount).ToArray());

            Assert.That(result.Success, Is.True);
            Assert.That(result.Kind, Is.EqualTo("array"));
            Assert.That(result.TotalCount, Is.EqualTo(totalCount));
            Assert.That(result.ReturnedCount, Is.EqualTo(returnedCount));
            Assert.That(result.Truncated, Is.EqualTo(truncated));
            Assert.That(result.Json, Does.Contain("\"total_count\":" + totalCount));
            Assert.That(result.Json, Does.Contain("\"returned_count\":" + returnedCount));
            Assert.That(result.Json, Does.Contain("\"truncated\":" + (truncated ? "true" : "false")));
            Assert.That(result.Utf8Bytes, Is.LessThanOrEqualTo(DynamicCSharpResultSerializer.MaxResultUtf8Bytes));
        }

        [Test]
        public void NestedAnonymousObjectPropagatesChildTruncationWithoutToStringFallback()
        {
            var value = new
            {
                Label = "sample",
                Values = Enumerable.Range(0, 101).ToArray(),
            };

            var result = DynamicCSharpResultSerializer.Serialize(value);

            Assert.That(result.Success, Is.True);
            Assert.That(result.Kind, Is.EqualTo("object"));
            Assert.That(result.TotalCount, Is.EqualTo(2));
            Assert.That(result.ReturnedCount, Is.EqualTo(2));
            Assert.That(result.Truncated, Is.True);
            Assert.That(result.Json, Does.Contain("\"properties\":{\"Label\":\"sample\",\"Values\":{\"kind\":\"array\""));
            Assert.That(result.Json, Does.Contain("\"total_count\":101,\"returned_count\":100,\"truncated\":true"));
            Assert.That(result.Json, Does.Not.Contain(value.ToString()));
        }

        [Test]
        public void MultibyteStringUsesUtf8BoundWithoutSplittingSurrogatePairs()
        {
            var value = string.Concat(Enumerable.Repeat("😀", 200));

            var result = DynamicCSharpResultSerializer.Serialize(value);

            Assert.That(result.Success, Is.True);
            Assert.That(result.Truncated, Is.True);
            Assert.That(result.TotalCount, Is.EqualTo(value.Length));
            Assert.That(result.ReturnedCount, Is.EqualTo(128));
            Assert.That(result.ReturnedCount % 2, Is.Zero);
            Assert.That(result.Json, Does.Contain("\"total_utf8_bytes\":" + Encoding.UTF8.GetByteCount(value)));
            Assert.That(result.Json, Does.Contain("\"returned_utf8_bytes\":256"));
            Assert.That(result.Json.IndexOf('\uFFFD'), Is.EqualTo(-1));
        }

        [Test]
        public void InvalidUtf16StringFailsTypedInsteadOfReportingCorruptedSuccess()
        {
            var result = DynamicCSharpResultSerializer.Serialize("\uD800");

            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorCode, Is.EqualTo("result_serialization_invalid_unicode"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"reason\":\"invalid_utf16\""));
        }

        [Test]
        public void DictionariesUseCanonicalOrdinalKeyOrder()
        {
            var value = new Hashtable
            {
                ["z"] = 2,
                ["a"] = 1,
            };

            var first = DynamicCSharpResultSerializer.Serialize(value);
            var second = DynamicCSharpResultSerializer.Serialize(value);

            Assert.That(first.Success, Is.True);
            Assert.That(first.Json, Is.EqualTo(second.Json));
            Assert.That(first.Json, Does.Contain("\"entries\":{\"a\":1,\"z\":2}"));
        }

        [Test]
        public void UnsupportedShapeFailsTypedWithoutCallingToString()
        {
            UnsupportedValue.ToStringCalls = 0;
            var value = new UnsupportedValue();

            var result = DynamicCSharpResultSerializer.Serialize(value);

            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorCode, Is.EqualTo("result_serialization_unsupported"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"reason\":\"unsupported_shape\""));
            Assert.That(UnsupportedValue.ToStringCalls, Is.Zero);
        }

        [Test]
        public void EnumerableIsConsumedOnceWhileComputingExactCount()
        {
            var value = new OneShotEnumerable(1545);

            var result = DynamicCSharpResultSerializer.Serialize(value);

            Assert.That(result.Success, Is.True);
            Assert.That(result.TotalCount, Is.EqualTo(1545));
            Assert.That(result.ReturnedCount, Is.EqualTo(100));
            Assert.That(result.Truncated, Is.True);
            Assert.That(value.GetEnumeratorCount, Is.EqualTo(1));
        }

        [Test]
        public void DictionaryEnumerationCannotExceedIndependentScanBound()
        {
            var value = new OverproducingDictionary();

            var result = DynamicCSharpResultSerializer.Serialize(value);

            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorCode, Is.EqualTo("result_serialization_unsupported"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"reason\":\"dictionary_enumeration_bound\""));
            Assert.That(value.MoveNextCount, Is.EqualTo(DynamicCSharpResultSerializer.MaxEnumerableScanItems + 1));
        }

        [TestCase("full")]
        [TestCase("compact")]
        public void TruncatedPresentationDoesNotReexecuteBody(string responseMode)
        {
            s_executeCount = 0;
            ResetOutput();
            var method = typeof(McpPr211DynamicCSharpResultSerializationTests).GetMethod(nameof(ExecuteOnce), BindingFlags.NonPublic | BindingFlags.Static);

            var response = EditorCSharpExecutionCommands.InvokeCompiledMethod(method, true, responseMode);

            Assert.That(response.Status, Is.EqualTo("ok"));
            Assert.That(s_executeCount, Is.EqualTo(1));
            Assert.That(response.DataJson, Does.Contain("\"response_mode\":\"" + responseMode + "\""));
            Assert.That(response.DataJson, Does.Contain("\"result_truncated\":true"));
            Assert.That(response.DataJson, Does.Contain("\"output_truncated\":true"));
            Assert.That(response.DataJson, Does.Contain("\"console_output_truncated\":false"));
            Assert.That(response.DataJson, Does.Contain("\"total_count\":101,\"returned_count\":100"));
        }

        [Test]
        public void SupportedWorstCaseResultStaysWithinCanonicalUtf8Limit()
        {
            var value = Enumerable.Range(0, 100)
                .Select(_ => string.Concat(Enumerable.Repeat("😀", 200)))
                .ToArray();

            var result = DynamicCSharpResultSerializer.Serialize(value);

            Assert.That(result.Success, Is.True);
            Assert.That(result.Truncated, Is.True);
            Assert.That(Encoding.UTF8.GetByteCount(result.Json), Is.EqualTo(result.Utf8Bytes));
            Assert.That(result.Utf8Bytes, Is.LessThanOrEqualTo(DynamicCSharpResultSerializer.MaxResultUtf8Bytes));
        }

        [Test]
        public void ConsoleOutputTruncationPreservesSurrogatePairs()
        {
            ResetOutput(2);
            EditorCSharpExecutionCommands.Print("A😀B");
            var method = typeof(McpPr211DynamicCSharpResultSerializationTests).GetMethod(nameof(ReturnNull), BindingFlags.NonPublic | BindingFlags.Static);

            var response = EditorCSharpExecutionCommands.InvokeCompiledMethod(method, true, "full");

            Assert.That(response.Status, Is.EqualTo("ok"));
            Assert.That(response.DataJson, Does.Contain("\"output\":[\"A\"]"));
            Assert.That(response.DataJson, Does.Contain("\"console_output_truncated\":true"));
            Assert.That(response.DataJson.IndexOf('\uD800'), Is.EqualTo(-1));
            Assert.That(response.DataJson.IndexOf('\uFFFD'), Is.EqualTo(-1));
        }

        [Test]
        public void InvalidConsoleUnicodeIsReplacedWithExplicitTruncationAuthority()
        {
            ResetOutput();
            EditorCSharpExecutionCommands.Print("\uD800");
            var method = typeof(McpPr211DynamicCSharpResultSerializationTests).GetMethod(nameof(ReturnNull), BindingFlags.NonPublic | BindingFlags.Static);

            var response = EditorCSharpExecutionCommands.InvokeCompiledMethod(method, true, "full");

            Assert.That(response.Status, Is.EqualTo("ok"));
            Assert.That(response.DataJson.IndexOf('\uFFFD'), Is.GreaterThanOrEqualTo(0));
            Assert.That(response.DataJson, Does.Contain("\"console_output_truncated\":true"));
        }

        [Test]
        public void ExplicitEmptyConsoleEntryIsPreserved()
        {
            ResetOutput();
            EditorCSharpExecutionCommands.Print(string.Empty);
            var method = typeof(McpPr211DynamicCSharpResultSerializationTests).GetMethod(nameof(ReturnNull), BindingFlags.NonPublic | BindingFlags.Static);

            var response = EditorCSharpExecutionCommands.InvokeCompiledMethod(method, true, "full");

            Assert.That(response.Status, Is.EqualTo("ok"));
            Assert.That(response.DataJson, Does.Contain("\"output\":[\"\"]"));
            Assert.That(response.DataJson, Does.Contain("\"console_output_truncated\":false"));
        }

        [Test]
        public void EmptyConsoleEntriesHaveIndependentBound()
        {
            ResetOutput();
            for (var index = 0; index < 257; index++)
            {
                EditorCSharpExecutionCommands.Print(string.Empty);
            }
            var method = typeof(McpPr211DynamicCSharpResultSerializationTests).GetMethod(nameof(ReturnNull), BindingFlags.NonPublic | BindingFlags.Static);

            var response = EditorCSharpExecutionCommands.InvokeCompiledMethod(method, true, "full");
            var outputStart = response.DataJson.IndexOf("\"output\":[", StringComparison.Ordinal) + "\"output\":[".Length;
            var outputEnd = response.DataJson.IndexOf("],\"console_output_truncated\"", outputStart, StringComparison.Ordinal);
            var output = response.DataJson.Substring(outputStart, outputEnd - outputStart);

            Assert.That(response.Status, Is.EqualTo("ok"));
            Assert.That(output.Split(',').Length, Is.EqualTo(256));
            Assert.That(response.DataJson, Does.Contain("\"console_output_truncated\":true"));
        }

        [Test]
        public void BoundedMetadataNameDoesNotSplitSurrogatePair()
        {
            var method = typeof(DynamicCSharpResultSerializer).GetMethod("BoundedName", BindingFlags.NonPublic | BindingFlags.Static);
            var value = new string('a', 127) + "😀suffix";

            var bounded = (string)method.Invoke(null, new object[] { value });

            Assert.That(bounded, Is.EqualTo(new string('a', 127)));
            Assert.That(bounded.IndexOf('\uD800'), Is.EqualTo(-1));
            Assert.That(bounded.IndexOf('\uFFFD'), Is.EqualTo(-1));
        }

        // PR234: source-local accounting and managed fake-method fixtures only.
        // No Roslyn compilation, historical receipt recovery, or host-cause claim.
        [TestCase(255, 255, false)]
        [TestCase(256, 256, false)]
        [TestCase(257, 256, true)]
        public void StringAnnotationBeginsOnlyBeyondTheEscapedContentBoundary(int length, int returned, bool truncated)
        {
            var value = new string('x', length);
            var prefix = value.Substring(0, returned);
            var result = DynamicCSharpResultSerializer.Serialize(value);

            AssertStringResult(result, value, prefix, truncated);
            Assert.That(EscapedContentBytes(prefix), Is.EqualTo(returned));
            foreach (var responseMode in new[] { "full", "compact" })
            {
                const string console = "ascii-boundary-console";
                InvokeControlled(nameof(ExecuteOnceStringFake), value, console, responseMode, true,
                    json => AssertInvocation(json, responseMode, result, console, false));
            }
        }

        [TestCase("full")]
        [TestCase("compact")]
        public void Synthetic1812To252PrefixNamesEscapedByteAuthorityWithoutClippingConsole(string responseMode)
        {
            var prefix = new string('a', 248) + new string('\n', 4);
            var value = prefix + new string('a', 1560);
            var result = DynamicCSharpResultSerializer.Serialize(value);

            Assert.That(value.Length, Is.EqualTo(1812));
            Assert.That(StrictUtf8.GetByteCount(value), Is.EqualTo(1812));
            Assert.That(EscapedContentBytes(value), Is.EqualTo(1816));
            Assert.That(prefix.Length, Is.EqualTo(252));
            Assert.That(StrictUtf8.GetByteCount(prefix), Is.EqualTo(252));
            Assert.That(EscapedContentBytes(prefix), Is.EqualTo(256));
            AssertStringResult(result, value, prefix, true);
            Assert.That(result.Utf8Bytes, Is.EqualTo(452));

            InvokeControlled(nameof(ExecuteOnceStringFake), value, value, responseMode, true,
                json => AssertInvocation(json, responseMode, result, value, false));
        }

        [TestCase("quote", 128, 256)]
        [TestCase("backslash", 128, 256)]
        [TestCase("newline", 128, 256)]
        [TestCase("control", 42, 252)]
        public void EscapedCharactersKeepRawCountersAndExposeOnlyTheStringLimit(string fixture, int returned, int escapedBytes)
        {
            char character;
            switch (fixture)
            {
                case "quote": character = '"'; break;
                case "backslash": character = '\\'; break;
                case "newline": character = '\n'; break;
                default: character = '\u0001'; break;
            }
            var value = new string(character, 300);
            var prefix = new string(character, returned);
            var result = DynamicCSharpResultSerializer.Serialize(value);

            AssertStringResult(result, value, prefix, true);
            Assert.That(StrictUtf8.GetByteCount(value), Is.EqualTo(300));
            Assert.That(StrictUtf8.GetByteCount(prefix), Is.EqualTo(returned));
            Assert.That(EscapedContentBytes(prefix), Is.EqualTo(escapedBytes));
            Assert.That(EscapedContentBytes(prefix + character), Is.GreaterThan(256));
        }

        [TestCase(64, false)]
        [TestCase(65, true)]
        [TestCase(200, true)]
        public void EmojiAnnotationPreservesTheExisting128CodeUnitPrefix(int emojiCount, bool truncated)
        {
            var value = string.Concat(Enumerable.Repeat("😀", emojiCount));
            var prefix = string.Concat(Enumerable.Repeat("😀", 64));
            var result = DynamicCSharpResultSerializer.Serialize(value);

            AssertStringResult(result, value, prefix, truncated);
            Assert.That(result.ReturnedCount, Is.EqualTo(128));
            Assert.That(result.ReturnedCount % 2, Is.Zero);
            Assert.That(StrictUtf8.GetByteCount(prefix), Is.EqualTo(256));
            Assert.That(EscapedContentBytes(prefix), Is.EqualTo(256));
            // NUnit's culture-sensitive substring constraint can treat the
            // replacement character as ignorable on Mono. Inspect code units.
            Assert.That(result.Json.IndexOf('\uFFFD'), Is.EqualTo(-1));
        }

        [TestCase("lone_high")]
        [TestCase("lone_low")]
        [TestCase("invalid_suffix")]
        public void InvalidStringUnicodeRemainsTypedFailureWithoutSuccessAnnotation(string fixture)
        {
            var value = fixture == "lone_high" ? "\uD800"
                : fixture == "lone_low" ? "\uDC00" : new string('a', 300) + "\uD800";
            var result = DynamicCSharpResultSerializer.Serialize(value);

            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorCode, Is.EqualTo("result_serialization_invalid_unicode"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"reason\":\"invalid_utf16\""));
            Assert.That(result.Json, Is.Empty);
            Assert.That(result.Utf8Bytes, Is.Zero);
            AssertNoStringAnnotation(result.ErrorDetailsJson);
            foreach (var responseMode in new[] { "full", "compact" })
            {
                // Do not print invalid Unicode: result failure and console
                // Unicode replacement have different existing contracts.
                InvokeControlled(nameof(ReturnParameterFake), value, null, responseMode, false, AssertNoStringAnnotation);
            }
        }

        [Test]
        public void NestedStringAnnotationsDoNotRedefineContainerCountsOrErrorReasons()
        {
            var plain = new string('s', 256);
            var longValue = new string('l', 257);
            var firstItem = new string('v', 257);
            var secondItem = new string('w', 258);
            var result = DynamicCSharpResultSerializer.Serialize(new
            {
                Plain = plain,
                Long = longValue,
                Values = new[] { firstItem, secondItem },
            });
            var expectedArray = "{\"kind\":\"array\",\"items\":["
                + ExpectedStringEnvelope(firstItem, firstItem.Substring(0, 256)) + ","
                + ExpectedStringEnvelope(secondItem, secondItem.Substring(0, 256))
                + "],\"total_count\":2,\"returned_count\":2,\"truncated\":true}";

            Assert.That(result.Success, Is.True);
            Assert.That(result.Kind, Is.EqualTo("object"));
            Assert.That(result.TotalCount, Is.EqualTo(3));
            Assert.That(result.ReturnedCount, Is.EqualTo(3));
            Assert.That(result.Truncated, Is.True);
            Assert.That(result.TruncationCount, Is.EqualTo(3));
            Assert.That(result.Json, Does.Contain("\"properties\":{\"Long\":"
                + ExpectedStringEnvelope(longValue, longValue.Substring(0, 256))
                + ",\"Plain\":" + FixtureJsonString(plain) + ",\"Values\":" + expectedArray + "}"));
            Assert.That(result.Json, Does.EndWith("\"total_count\":3,\"returned_count\":3,\"truncated\":true}"));
            Assert.That(CountOccurrences(result.Json, StringAnnotation), Is.EqualTo(3));
            Assert.That(CountOccurrences(result.Json, "\"reason\":"), Is.EqualTo(3));
            Assert.That(CountOccurrences(result.Json, "\"applied_limit\":"), Is.EqualTo(3));
            AssertResultAccounting(result);

            UnsupportedValue.ToStringCalls = 0;
            try
            {
                var failure = DynamicCSharpResultSerializer.Serialize(new { Value = new UnsupportedValue() });
                Assert.That(failure.Success, Is.False);
                Assert.That(failure.ErrorCode, Is.EqualTo("result_serialization_unsupported"));
                Assert.That(failure.ErrorDetailsJson, Does.Contain("\"reason\":\"unsupported_shape\""));
                AssertNoStringAnnotation(failure.ErrorDetailsJson);
                Assert.That(UnsupportedValue.ToStringCalls, Is.Zero);
            }
            finally
            {
                UnsupportedValue.ToStringCalls = 0;
            }
        }

        [Test]
        public void OneHundredAnnotatedEmojiStringsRecountTheCanonicalResultBytes()
        {
            var value = string.Concat(Enumerable.Repeat("😀", 200));
            var prefix = string.Concat(Enumerable.Repeat("😀", 64));
            var leaf = ExpectedStringEnvelope(value, prefix);
            var result = DynamicCSharpResultSerializer.Serialize(Enumerable.Repeat(value, 100).ToArray());
            var expected = "{\"kind\":\"array\",\"items\":[" + string.Join(",", Enumerable.Repeat(leaf, 100).ToArray())
                + "],\"total_count\":100,\"returned_count\":100,\"truncated\":true}";

            Assert.That(result.Success, Is.True);
            Assert.That(result.Kind, Is.EqualTo("array"));
            Assert.That(result.TotalCount, Is.EqualTo(100));
            Assert.That(result.ReturnedCount, Is.EqualTo(100));
            Assert.That(result.Truncated, Is.True);
            Assert.That(result.TruncationCount, Is.EqualTo(100));
            Assert.That(result.Json, Is.EqualTo(expected));
            Assert.That(CountOccurrences(result.Json, StringAnnotation), Is.EqualTo(100));
            Assert.That(CountOccurrences(result.Json, "\"applied_limit\":256"), Is.EqualTo(100));
            Assert.That(result.Utf8Bytes, Is.EqualTo(45182));
            Assert.That(DynamicCSharpResultSerializer.MaxResultUtf8Bytes, Is.EqualTo(65536));
            AssertResultAccounting(result);
        }

        [TestCase("full", false, false)]
        [TestCase("full", false, true)]
        [TestCase("full", true, false)]
        [TestCase("full", true, true)]
        [TestCase("compact", false, false)]
        [TestCase("compact", false, true)]
        [TestCase("compact", true, false)]
        [TestCase("compact", true, true)]
        public void ConsoleAndResultLimitsStayIndependentWithOneBodyInvocation(string responseMode, bool consoleTruncated, bool resultTruncated)
        {
            var value = new string('x', resultTruncated ? 257 : 256);
            var console = new string('c', consoleTruncated ? ConsoleBudget + 1 : ConsoleBudget);
            var result = DynamicCSharpResultSerializer.Serialize(value);

            AssertStringResult(result, value, new string('x', 256), resultTruncated);
            InvokeControlled(nameof(ExecuteOnceStringFake), value, console, responseMode, true,
                json => AssertInvocation(json, responseMode, result, new string('c', ConsoleBudget), consoleTruncated));
        }

        [TestCase(68, 65521, true)]
        [TestCase(69, 65646, false)]
        public void AnnotatedWholeResultEnforcesTheUnchangedByteCapWithoutRetrimming(int keyLength, int modeledBytes, bool success)
        {
            var value = new string('x', 257);
            var first = BoundedStringDictionary(100, keyLength, value);
            var second = BoundedStringDictionary(25, keyLength, value);
            var root = new Hashtable { ["a"] = first, ["b"] = second };

            Assert.That(1 + 2 + first.Count + second.Count, Is.EqualTo(128));
            Assert.That(DynamicCSharpResultSerializer.MaxVisitedValues, Is.EqualTo(128));
            Assert.That(DynamicCSharpResultSerializer.MaxResultUtf8Bytes, Is.EqualTo(65536));
            Assert.That(StrictUtf8.GetByteCount(ExpectedStringEnvelope(value, new string('x', 256))), Is.EqualTo(450));
            Assert.That(125 * (keyLength + 454) + 271, Is.EqualTo(modeledBytes));

            var result = DynamicCSharpResultSerializer.Serialize(root);

            Assert.That(result.Success, Is.EqualTo(success));
            Assert.That(result.Kind, Is.EqualTo("dictionary"));
            Assert.That(result.TotalCount, Is.EqualTo(2));
            Assert.That(result.ReturnedCount, Is.EqualTo(2));
            if (!success)
            {
                Assert.That(modeledBytes, Is.GreaterThan(65536));
                Assert.That(result.ErrorCode, Is.EqualTo("result_serialization_too_large"));
                Assert.That(result.ErrorDetailsJson, Does.Contain("\"reason\":\"max_result_utf8_bytes\""));
                Assert.That(result.ErrorDetailsJson, Does.Contain("\"max_result_utf8_bytes\":65536"));
                Assert.That(result.Json, Is.Empty);
                Assert.That(result.Utf8Bytes, Is.Zero);
                AssertNoStringAnnotation(result.ErrorDetailsJson);
                return;
            }

            Assert.That(result.Truncated, Is.True);
            Assert.That(result.TruncationCount, Is.EqualTo(125));
            Assert.That(result.Utf8Bytes, Is.EqualTo(modeledBytes));
            Assert.That(result.Json, Does.Contain("\"total_count\":100,\"returned_count\":100,\"truncated\":true"));
            Assert.That(result.Json, Does.Contain("\"total_count\":25,\"returned_count\":25,\"truncated\":true"));
            Assert.That(CountOccurrences(result.Json, StringAnnotation), Is.EqualTo(125));
            Assert.That(CountOccurrences(result.Json, "\"applied_limit\":256"), Is.EqualTo(125));
            AssertResultAccounting(result);
        }

        static void AssertStringResult(DynamicCSharpSerializationResult result, string value, string prefix, bool truncated)
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.Kind, Is.EqualTo("string"));
            Assert.That(result.TotalCount, Is.EqualTo(value.Length));
            Assert.That(result.ReturnedCount, Is.EqualTo(prefix.Length));
            Assert.That(result.Truncated, Is.EqualTo(truncated));
            Assert.That(result.TruncationCount, Is.EqualTo(truncated ? 1 : 0));
            Assert.That(result.Json, Is.EqualTo(truncated ? ExpectedStringEnvelope(value, prefix) : FixtureJsonString(value)));
            Assert.That(EscapedContentBytes(prefix), Is.LessThanOrEqualTo(256));
            if (!truncated) AssertNoStringAnnotation(result.Json);
            AssertResultAccounting(result);
        }

        static void AssertResultAccounting(DynamicCSharpSerializationResult result)
        {
            Assert.That(result.Success, Is.True);
            var bytes = StrictUtf8.GetByteCount(result.Json);
            Assert.That(result.Utf8Bytes, Is.EqualTo(bytes));
            Assert.That(bytes, Is.LessThanOrEqualTo(65536));
            var summary = "{\"kind\":" + FixtureJsonString(result.Kind)
                + ",\"total_count\":" + Number(result.TotalCount) + ",\"returned_count\":" + Number(result.ReturnedCount)
                + ",\"truncated\":" + Bool(result.Truncated) + ",\"truncation_count\":" + Number(result.TruncationCount)
                + ",\"utf8_bytes\":" + Number(bytes) + "}";
            Assert.That(result.SummaryJson(), Is.EqualTo(summary));
            AssertNoStringAnnotation(result.SummaryJson());
        }

        static void AssertInvocation(string json, string responseMode, DynamicCSharpSerializationResult result, string console, bool consoleTruncated)
        {
            // Decode only generated output, not arbitrary input validation.
            var envelope = UnityEngine.JsonUtility.FromJson<InvocationEnvelope>(json);
            Assert.That(envelope, Is.Not.Null);
            Assert.That(envelope.response_mode, Is.EqualTo(responseMode));
            Assert.That(envelope.output, Is.EqualTo(new[] { console }));
            Assert.That(envelope.console_output_truncated, Is.EqualTo(consoleTruncated));
            Assert.That(envelope.result_truncated, Is.EqualTo(result.Truncated));
            Assert.That(envelope.output_truncated, Is.EqualTo(consoleTruncated || result.Truncated));
            Assert.That(json, Does.Contain("\"console_output_truncated\":" + Bool(consoleTruncated)));
            Assert.That(json, Does.Contain("\"result_truncated\":" + Bool(result.Truncated)));
            Assert.That(json, Does.Contain("\"output_truncated\":" + Bool(consoleTruncated || result.Truncated)));
            Assert.That(json, Does.Contain(result.Json));
            Assert.That(json, Does.Contain("\"result_summary\":" + result.SummaryJson()));
            AssertResultAccounting(result);
        }

        static void InvokeControlled(string methodName, object value, string console, string responseMode, bool success, Action<string> assertData)
        {
            try
            {
                s_parameter = value;
                s_consoleText = console;
                s_executeCount = 0;
                ResetOutput(ConsoleBudget);
                var method = typeof(McpPr211DynamicCSharpResultSerializationTests).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(method, Is.Not.Null);

                var response = EditorCSharpExecutionCommands.InvokeCompiledMethod(method, true, responseMode);

                Assert.That(response.Status, Is.EqualTo(success ? "ok" : "error"));
                Assert.That(s_executeCount, Is.EqualTo(1));
                assertData(response.DataJson ?? string.Empty);
            }
            finally
            {
                s_parameter = null;
                s_consoleText = null;
                s_executeCount = 0;
                ResetOutput();
            }
        }

        static string ExecuteOnceStringFake()
        {
            s_executeCount++;
            if (s_consoleText != null) EditorCSharpExecutionCommands.Print(s_consoleText);
            return (string)s_parameter;
        }

        static object ReturnParameterFake()
        {
            s_executeCount++;
            return s_parameter;
        }

        static Hashtable BoundedStringDictionary(int count, int keyLength, string value)
        {
            var result = new Hashtable();
            for (var index = 0; index < count; index++)
            {
                var key = index.ToString("D2", CultureInfo.InvariantCulture) + new string('k', keyLength - 2);
                result.Add(key, value);
            }
            return result;
        }

        static string ExpectedStringEnvelope(string value, string prefix)
        {
            return "{\"kind\":\"string\",\"value\":" + FixtureJsonString(prefix)
                + ",\"total_count\":" + Number(value.Length) + ",\"returned_count\":" + Number(prefix.Length)
                + ",\"total_utf8_bytes\":" + Number(StrictUtf8.GetByteCount(value))
                + ",\"returned_utf8_bytes\":" + Number(StrictUtf8.GetByteCount(prefix))
                + ",\"truncated\":true,\"reason\":\"" + StringLimitReason + "\",\"applied_limit\":256}";
        }

        static int EscapedContentBytes(string value)
        {
            return StrictUtf8.GetByteCount(FixtureJsonString(value)) - 2;
        }

        static string FixtureJsonString(string value)
        {
            var builder = new StringBuilder("\"");
            foreach (var character in value)
            {
                switch (character)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < ' ') builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        else builder.Append(character);
                        break;
                }
            }
            return builder.Append('"').ToString();
        }

        static int CountOccurrences(string value, string marker)
        {
            var count = 0;
            var offset = 0;
            while ((offset = value.IndexOf(marker, offset, StringComparison.Ordinal)) >= 0)
            {
                count++;
                offset += marker.Length;
            }
            return count;
        }

        static void AssertNoStringAnnotation(string json)
        {
            Assert.That(json, Does.Not.Contain(StringAnnotation));
            Assert.That(json, Does.Not.Contain("\"applied_limit\":"));
        }

        static string Number(int value) { return value.ToString(CultureInfo.InvariantCulture); }
        static string Bool(bool value) { return value ? "true" : "false"; }

        [Serializable]
        public sealed class InvocationEnvelope
        {
            public string response_mode = string.Empty;
            public string[] output = null;
            public bool console_output_truncated = false;
            public bool result_truncated = false;
            public bool output_truncated = false;
        }

        static object ExecuteOnce()
        {
            s_executeCount++;
            return Enumerable.Range(0, 101).ToArray();
        }

        static object ReturnNull()
        {
            return null;
        }

        static void ResetOutput(int limit = 4000)
        {
            var method = typeof(EditorCSharpExecutionCommands).GetMethod("ResetOutput", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            method.Invoke(null, new object[] { limit });
        }

        sealed class UnsupportedValue
        {
            internal static int ToStringCalls;

            public override string ToString()
            {
                ToStringCalls++;
                return "must-not-be-used";
            }
        }

        sealed class OneShotEnumerable : IEnumerable
        {
            readonly int m_count;

            public OneShotEnumerable(int count)
            {
                m_count = count;
            }

            public int GetEnumeratorCount { get; private set; }

            public IEnumerator GetEnumerator()
            {
                GetEnumeratorCount++;
                if (GetEnumeratorCount > 1)
                {
                    throw new InvalidOperationException("Enumerable was requested more than once.");
                }

                return Values().GetEnumerator();
            }

            IEnumerable Values()
            {
                for (var index = 0; index < m_count; index++)
                {
                    yield return index;
                }
            }
        }

        sealed class OverproducingDictionary : IDictionary
        {
            public int MoveNextCount { get; private set; }
            public int Count => 1;
            public bool IsFixedSize => true;
            public bool IsReadOnly => true;
            public bool IsSynchronized => false;
            public object SyncRoot => this;
            public ICollection Keys => throw new NotSupportedException();
            public ICollection Values => throw new NotSupportedException();
            public object this[object key]
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public void Add(object key, object value) => throw new NotSupportedException();
            public void Clear() => throw new NotSupportedException();
            public bool Contains(object key) => false;
            public void CopyTo(Array array, int index) => throw new NotSupportedException();
            public void Remove(object key) => throw new NotSupportedException();
            public IDictionaryEnumerator GetEnumerator() => new Enumerator(this);
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            sealed class Enumerator : IDictionaryEnumerator
            {
                readonly OverproducingDictionary m_owner;
                int m_index = -1;

                public Enumerator(OverproducingDictionary owner)
                {
                    m_owner = owner;
                }

                public DictionaryEntry Entry => new DictionaryEntry(m_index, m_index);
                public object Key => m_index;
                public object Value => m_index;
                public object Current => Entry;

                public bool MoveNext()
                {
                    m_owner.MoveNextCount++;
                    m_index++;
                    return m_index < DynamicCSharpResultSerializer.MaxEnumerableScanItems + 10;
                }

                public void Reset()
                {
                    m_index = -1;
                }
            }
        }
    }
}
