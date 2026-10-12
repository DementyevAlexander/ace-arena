using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Commands;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class McpPr120DynamicCSharpDiagnosticsTests
    {
        [TestCase("using Foo;", true)]
        [TestCase("global using Foo;", true)]
        [TestCase("using static Foo.Bar;", true)]
        [TestCase("using Alias = Foo.Bar;", true)]
        [TestCase("using var = Foo.Bar;", true)]
        [TestCase("using var;", true)]
        [TestCase("using var.Foo;", true)]
        [TestCase("global using var.Foo;", true)]
        [TestCase("using static Foo.Generic<int>;", true)]
        [TestCase("global using static Foo.Outer<int>.Inner<string>;", true)]
        [TestCase("using \\u0046oo.Bar;", true)]
        [TestCase("using\\u0046oo = Foo.Bar;", false)]
        [TestCase("// comment\n/* another */\nusing Foo.Bar;", true)]
        [TestCase("using var value = GetValue();", false)]
        [TestCase("using var.Foo value = GetValue();", false)]
        [TestCase("using (var value = GetValue()) { }", false)]
        [TestCase("using IDisposable value = GetValue();", false)]
        [TestCase("using Foo.Generic<int> value = GetValue();", false)]
        public void LeadingUsingDirectiveShapeDoesNotRejectMethodBodyUsingStatements(string code, bool expected)
        {
            Assert.That(DynamicCSharpDiagnosticClassifier.HasLeadingUsingDirective(code), Is.EqualTo(expected));
        }

        [Test]
        public void SyntaxClassificationWinsOverApiLookingCompanionDiagnostics()
        {
            var category = DynamicCSharpDiagnosticClassifier.Classify(
                new List<string>
                {
                    "Identifier expected",
                    "The type or namespace name 'CallerType' could not be found (are you missing an assembly reference?)",
                },
                new List<string> { "CS1001", "CS0246" },
                false);

            Assert.That(category, Is.EqualTo("wrapper_syntax_error"));
        }

        [Test]
        public void UnknownCallerTypeFallsBackToCallerCompileError()
        {
            var category = DynamicCSharpDiagnosticClassifier.Classify(
                new List<string> { "The type or namespace name 'CallerType' could not be found (are you missing an assembly reference?)" },
                new List<string> { "CS0246" },
                false);

            Assert.That(category, Is.EqualTo("caller_compile_error"));
        }

        [Test]
        public void UnityQualifiedApiDiagnosticUsesUnsupportedApiShape()
        {
            var category = DynamicCSharpDiagnosticClassifier.Classify(
                new List<string> { "'UnityEditor.EditorApplication' does not contain a definition for 'MissingMember'" },
                new List<string> { "CS1061" },
                false);

            Assert.That(category, Is.EqualTo("unsupported_unity_api_shape"));
        }

        [Test]
        public void CallerTypeContainingUnityEditorTextDoesNotEnableReflectionRecovery()
        {
            var category = DynamicCSharpDiagnosticClassifier.Classify(
                new List<string> { "The type or namespace name 'MissingUnityEditorType' could not be found" },
                new List<string> { "CS0246" },
                false);

            Assert.That(category, Is.EqualTo("caller_compile_error"));
        }

        [Test]
        public void UnityApiTextMustAlignWithItsDiagnosticIdAndTokenBoundary()
        {
            var misaligned = DynamicCSharpDiagnosticClassifier.Classify(
                new List<string> { "#error UnityEditor.Foo", "The type 'CallerType' could not be found" },
                new List<string> { "CS1029", "CS0246" },
                false);
            var embedded = DynamicCSharpDiagnosticClassifier.Classify(
                new List<string> { "The type 'MyUnityEditor.Foo' could not be found" },
                new List<string> { "CS0246" },
                false);

            Assert.That(misaligned, Is.EqualTo("caller_compile_error"));
            Assert.That(embedded, Is.EqualTo("caller_compile_error"));
        }

        [Test]
        public void ObjectReturnDiagnosticKeepsItsExistingCategory()
        {
            var category = DynamicCSharpDiagnosticClassifier.Classify(
                new List<string> { "CS0126: An object of a type convertible to 'object' is required" },
                new List<string> { "CS0126" },
                false);

            Assert.That(category, Is.EqualTo("object_return_contract_violation"));
        }

        [TestCase("CS", "compiler_error")]
        [TestCase("CS1001", "CS1001")]
        [TestCase("CS10A1", "compiler_error")]
        public void DiagnosticIdsAreBoundedAndNumeric(string value, string expected)
        {
            Assert.That(DynamicCSharpDiagnosticClassifier.NormalizeDiagnosticId(value), Is.EqualTo(expected));
        }

        [Test]
        public void LeadingUsingDirectiveReturnsStructuredCompileFailureBeforeRoslyn()
        {
            var context = new McpCommandContext(null, "test", "editor.execute_csharp.readonly", "{\"code\":\"using Foo;\"}", "{}");
            var result = EditorCSharpExecutionCommands.ExecuteReadonly(context);

            Assert.That(result.ErrorCode, Is.EqualTo("compilation_failed"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"compilation_diagnostic_category\":\"wrapper_syntax_error\""));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"compilation_diagnostic_kind\":\"wrapper_syntax_error\""));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"diagnostic_ids\":[]"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"diagnostics_truncated\":false"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"source_shape\":\"leading_using_directive\""));
        }

        [Test]
        public void CompileFailureDetailsPreserveLegacyCategoryAndBoundDiagnosticIds()
        {
            var method = typeof(EditorCSharpExecutionCommands).GetMethod("CompilationFailedDetailsJson", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);

            var messages = new List<string> { "one", "two", "three", "four", "five", "six" };
            var ids = new List<string> { "CS0246", "CS10A1", "CS0246", "CS0246", "CS0246", "CS9999" };
            var details = (string)method.Invoke(null, new object[] { messages, ids, string.Empty });

            Assert.That(details, Does.Contain("\"compilation_diagnostic_category\":\"compile_error\""));
            Assert.That(details, Does.Contain("\"compilation_diagnostic_kind\":\"caller_compile_error\""));
            Assert.That(details, Does.Contain("\"diagnostic_count\":6"));
            Assert.That(details, Does.Contain("\"diagnostic_ids\":[\"CS0246\",\"compiler_error\",\"CS0246\",\"CS0246\",\"CS0246\"]"));
            Assert.That(details, Does.Contain("\"diagnostics_truncated\":true"));
            Assert.That(details, Does.Not.Contain("CS9999"));
        }

        [Test]
        public void DynamicReferenceInventoryIncludesNetStandard21AndIsSorted()
        {
            var method = typeof(EditorCSharpExecutionCommands).GetMethod("GetAssemblyPaths", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);

            EditorCSharpExecutionCommands.ResetCompilerCache();
            var paths = (string[])method.Invoke(null, null);
            Assert.That(paths, Is.Not.Empty);

            var foundNetStandard21 = false;
            for (var index = 0; index < paths.Length; index++)
            {
                Assert.That(Path.IsPathRooted(paths[index]), Is.True);
                if (index > 0)
                {
                    Assert.That(string.Compare(paths[index - 1], paths[index], StringComparison.OrdinalIgnoreCase), Is.LessThanOrEqualTo(0));
                }

                var name = AssemblyName.GetAssemblyName(paths[index]);
                if (string.Equals(name.Name, "netstandard", StringComparison.OrdinalIgnoreCase) && name.Version != null && name.Version.Major == 2 && name.Version.Minor == 1)
                {
                    foundNetStandard21 = true;
                }
            }

            Assert.That(foundNetStandard21, Is.True);
        }

        [Test]
        public void CompilationFinishedInvalidatesDynamicReferenceInventory()
        {
            var pathsField = typeof(EditorCSharpExecutionCommands).GetField("s_assemblyPaths", BindingFlags.NonPublic | BindingFlags.Static);
            var callback = typeof(EditorCSharpExecutionCommands).GetMethod("OnCompilationFinished", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(pathsField, Is.Not.Null);
            Assert.That(callback, Is.Not.Null);

            pathsField.SetValue(null, new[] { "sentinel" });
            callback.Invoke(null, new object[] { null });

            Assert.That(pathsField.GetValue(null), Is.Null);
        }
    }
}
